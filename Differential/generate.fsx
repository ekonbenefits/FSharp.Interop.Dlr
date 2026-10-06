// Writes Cases/*.g.fs: every member the grammar below builds, up to its bound (#200). Each case
// binds one or two values of one name, reaches each from a dlr { } block one way or another, and
// returns what the block read; every binding has a distinct value, so a wrong binding cannot
// read the right number by chance.
//   dotnet fsi generate.fsx

open System
open System.IO

let indent (lines: string list) = lines |> List.map (fun l -> "    " + l)

/// The bound value's type: an int, or a struct (read back as a copy; Support.S).
type Ty =
    { Name: string
      Annotation: string
      /// A value of the type from an int expression.
      Of: string -> string
      /// The int a value of the type holds.
      Get: string -> string }

let int' = { Name = "int"; Annotation = "int"; Of = id; Get = id }
let struct' = { Name = "struct"; Annotation = "S"; Of = sprintf "S(%s)"; Get = sprintf "(%s).V" }

/// How a value named `n` is bound: its lines before the rest of the member, given the rest.
type Binding =
    { Name: string
      /// The member's parameter (outer binding only): no lines, and a parameter of the case.
      IsParam: bool
      IsMutable: bool
      Wrap: Ty -> string -> int -> string list -> string list }

/// The value of binding `k` (1 outer, 2 inner), distinct from every other.
let values =
    [ "pure", (fun k -> sprintf "seed * 10 + %d" k)
      "call", (fun k -> sprintf "Ticks.Next() * 1000 + %d" k)
      "literal", (fun k -> sprintf "%d" (500 + k))
      "alias", (fun k -> if k = 1 then "seed" else "seed2") ]

let binding name isMutable wrap = { Name = name; IsParam = false; IsMutable = isMutable; Wrap = wrap }

let bindings : Binding list =
    [ for name, v in values do
        yield binding ("let-" + name) false (fun t n k rest -> sprintf "let %s = %s" n (t.Of (v k)) :: rest)
      for name, v in values |> List.filter (fun (n, _) -> n = "pure" || n = "call") do
        yield binding ("mutable-" + name) true (fun t n k rest ->
            [ sprintf "let mutable %s = %s" n (t.Of (v k)); sprintf "%s <- %s" n (t.Of (t.Get n + " + 1")) ] @ rest)
      yield binding "tuple" false (fun t n k rest -> sprintf "let (%s, _t%d) = (%s, 0)" n k (t.Of (sprintf "seed * 10 + %d" k)) :: rest)
      yield binding "match" false (fun t n k rest ->
          [ sprintf "match Some (%s) with" (t.Of (sprintf "seed * 10 + %d" k)); sprintf "| Some %s ->" n ] @ indent rest @ [ "| None -> \"none\"" ])
      yield binding "lambda" false (fun t n k rest ->
          [ sprintf "(fun (%s: %s) ->" n t.Annotation ] @ indent rest @ [ sprintf ") (%s)" (t.Of (sprintf "seed * 10 + %d" k)) ])
      yield binding "for" false (fun t n k rest ->
          [ sprintf "let mutable res%d = \"\"" k; sprintf "for %s in [ %s ] do" n (t.Of (sprintf "seed * 10 + %d" k)); sprintf "    res%d <-" k ]
          @ indent (indent rest) @ [ sprintf "res%d" k ])
      yield { Name = "param"; IsParam = true; IsMutable = false; Wrap = fun _ _ _ rest -> rest } ]

/// How the block reaches binding `k`: definitions placed right after the binding, and the int
/// expression the block reads.
type Reach =
    { Name: string
      Direct: bool
      NeedsMutable: bool
      Defs: Ty -> string -> int -> string list
      Read: Ty -> string -> int -> string }

let reaches =
    [ { Name = "direct"; Direct = true; NeedsMutable = false; Defs = (fun _ _ _ -> []); Read = fun t n _ -> t.Get n }
      { Name = "fun"; Direct = false; NeedsMutable = false
        Defs = (fun _ n k -> [ sprintf "let f%d () = %s" k n ]); Read = fun t _ k -> t.Get (sprintf "f%d ()" k) }
      { Name = "fun-twice"; Direct = false; NeedsMutable = false
        Defs = (fun _ n k -> [ sprintf "let f%d () = %s" k n ]); Read = fun t _ k -> sprintf "(%s + %s)" (t.Get (sprintf "f%d ()" k)) (t.Get (sprintf "f%d ()" k)) }
      { Name = "alias"; Direct = false; NeedsMutable = false
        Defs = (fun _ n k -> [ sprintf "let a%d = %s" k n ]); Read = fun t _ k -> t.Get (sprintf "a%d" k) }
      { Name = "rec"; Direct = false; NeedsMutable = false
        Defs = (fun _ n k -> [ sprintf "let rec r%d i = if i = 0 then %s else r%d (i - 1)" k n k ]); Read = fun t _ k -> t.Get (sprintf "r%d 2" k) }
      { Name = "lambda-value"; Direct = false; NeedsMutable = false
        Defs = (fun _ n k -> [ sprintf "let g%d = fun () -> %s" k n ]); Read = fun t _ k -> t.Get (sprintf "g%d ()" k) }
      { Name = "setter"; Direct = false; NeedsMutable = true
        Defs = (fun t n k -> [ sprintf "let s%d () = %s <- %s; %s" k n (t.Of (t.Get n + " + 100")) n ]); Read = fun t _ k -> t.Get (sprintf "s%d ()" k) } ]

/// Where the block sits, and where in it the reads are: the member's last expression, a
/// once-called local function's, a lambda's applied on the spot; the reads in a loop or a `try`
/// in the block (nested delegates).
let placements =
    let plain (echo: string) = [ sprintf "dlr { return %s }" echo ]
    [ "top", plain
      "local-fun", (fun echo -> [ "let run () : string =" ] @ indent (plain echo) @ [ "run ()" ])
      "applied-lambda", (fun echo -> [ "(fun () ->" ] @ indent [ sprintf "(dlr { return %s } : string)) ()" echo ])
      "block-loop", (fun echo ->
          [ "dlr {"; "    let mutable r = \"\""; "    for i in [ 1 ] do"; sprintf "        r <- %s" echo; "    return r }" ])
      "block-try", (fun echo ->
          [ "dlr {"; "    try"; sprintf "        return %s" echo; "    with _ -> return \"caught\" }" ]) ]

let fits (b: Binding) (r: Reach) = not r.NeedsMutable || b.IsMutable

type Case = { Id: string; Lines: string list; Params: string }

let echo (reads: string list) = sprintf "o?Echo(%s)" (String.Join(", ", reads))

let make id (t: Ty) (n: string) (outer: (Binding * Reach) option) (inner: Binding * Reach) distractor placement =
    let ib, ir = inner
    let reads = [ match outer with Some(_, r) -> yield r.Read t n 1 | None -> ()
                  yield ir.Read t n 2
                  if distractor then yield "d" ]
    let place = placements |> List.find (fst >> (=) placement) |> snd
    let tail =
        [ if distractor then yield sprintf "let d = [ 1 ] |> List.map (fun %s -> %s + 1000) |> List.head" n n ]
        @ place (echo reads)
    let innerLines = ib.Wrap t n 2 (ir.Defs t n 2 @ tail)
    let lines =
        match outer with
        | Some(ob, orr) -> ob.Wrap t n 1 (orr.Defs t n 1 @ innerLines)
        | None -> innerLines
    let isParam = match outer with Some(ob, _) -> ob.IsParam | None -> ib.IsParam
    let parameters = "(o: obj) (seed: int) (seed2: int)" + (if isParam then sprintf " (%s: %s)" n t.Annotation else "")
    { Id = id; Lines = lines; Params = parameters }

let cases =
    [ // One value, every way in, with and without an unrelated lambda's parameter of its name; of
      // each type, and named `x`, `Data` (the machine has a field of its own by that name) or
      // `matchValue` (a name the compiler gives its own locals).
      for t in [ int'; struct' ] do
        for n in [ "x"; "Data"; "matchValue" ] do
          for b in bindings do
            for r in reaches do
              if fits b r then
                for p, _ in placements do
                  for d in [ false; true ] ->
                    make (sprintf "one_%s_%s_%s_%s_%s%s" t.Name n b.Name r.Name p (if d then "_distractor" else "")) t n None (b, r) d p
      // Two `x`s: the outer reached indirectly (the inner shadows it), the inner any way; structs
      // at the top only.
      for t in [ int'; struct' ] do
        for ob in bindings do
          for orr in reaches do
            if not orr.Direct && fits ob orr then
              for ib in bindings do
                if not ib.IsParam then
                  for ir in reaches do
                    if fits ib ir then
                      for p, _ in placements do
                        if t.Name = "int" || p = "top" then
                          yield make (sprintf "two_%s_%s_%s__%s_%s_%s" t.Name ob.Name orr.Name ib.Name ir.Name p) t "x" (Some(ob, orr)) (ib, ir) false p ]

let identifier (s: string) = s.Replace("-", "_")

let dir = Path.Combine(__SOURCE_DIRECTORY__, "Cases")
if Directory.Exists dir then Directory.Delete(dir, true)
Directory.CreateDirectory dir |> ignore
let perFile = 300
cases
|> List.chunkBySize perFile
|> List.iteri (fun i chunk ->
    let body =
        [ yield "// Generated by generate.fsx: do not edit."
          yield sprintf "module Differential.Cases%03d" i
          yield ""
          yield "open FSharp.Interop.Dlr"
          yield "open Differential.Support"
          yield ""
          for c in chunk do
            yield "[<ReflectedDefinition>]"
            yield sprintf "let %s %s : string =" (identifier c.Id) c.Params
            yield! indent c.Lines
            yield "" ]
    File.WriteAllLines(Path.Combine(dir, sprintf "Cases%03d.g.fs" i), body))
printfn "%d cases in %d files" cases.Length ((cases.Length + perFile - 1) / perFile)
