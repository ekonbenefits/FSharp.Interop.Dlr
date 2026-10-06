// Writes Cases/*.g.fs: every member the grammar below builds, up to its bound (#200). Each case
// binds one or two values named `x`, reaches each from a dlr { } block one way or another, and
// returns what the block read; every binding has a distinct value, so a wrong binding cannot
// read the right number by chance.
//   dotnet fsi generate.fsx

open System
open System.IO

/// How a value named `x` is bound: its lines before the rest of the member, given the rest.
type Binding =
    { Name: string
      /// The member's parameter `x` (outer binding only): no lines, and a parameter of the case.
      IsParam: bool
      IsMutable: bool
      Wrap: int -> string list -> string list }

let indent (lines: string list) = lines |> List.map (fun l -> "    " + l)

/// The value of binding `k` (1 outer, 2 inner), distinct from every other.
let values =
    [ "pure", (fun k -> sprintf "seed * 10 + %d" k)
      "call", (fun k -> sprintf "Ticks.Next() * 1000 + %d" k)
      "literal", (fun k -> sprintf "%d" (500 + k))
      "alias", (fun k -> if k = 1 then "seed" else "seed2") ]

let binding name isMutable wrap = { Name = name; IsParam = false; IsMutable = isMutable; Wrap = wrap }

let bindings : Binding list =
    [ for name, v in values do
        yield binding ("let-" + name) false (fun k rest -> sprintf "let x = %s" (v k) :: rest)
      for name, v in values |> List.filter (fun (n, _) -> n = "pure" || n = "call") do
        yield binding ("mutable-" + name) true (fun k rest -> [ sprintf "let mutable x = %s" (v k); "x <- x + 1" ] @ rest)
      yield binding "tuple" false (fun k rest -> sprintf "let (x, _t%d) = (seed * 10 + %d, 0)" k k :: rest)
      yield binding "match" false (fun k rest ->
          [ sprintf "match Some (seed * 10 + %d) with" k; "| Some x ->" ] @ indent rest @ [ "| None -> \"none\"" ])
      yield binding "lambda" false (fun k rest -> [ "(fun (x: int) ->" ] @ indent rest @ [ sprintf ") (seed * 10 + %d)" k ])
      yield binding "for" false (fun k rest ->
          [ sprintf "let mutable res%d = \"\"" k; sprintf "for x in [ seed * 10 + %d ] do" k; sprintf "    res%d <-" k ]
          @ indent (indent rest) @ [ sprintf "res%d" k ])
      yield { Name = "param"; IsParam = true; IsMutable = false; Wrap = fun _ rest -> rest } ]

/// How the block reaches binding `k`: definitions placed right after the binding, and the
/// expression the block reads.
type Reach =
    { Name: string
      Direct: bool
      NeedsMutable: bool
      Defs: int -> string list
      Read: int -> string }

let reaches =
    [ { Name = "direct"; Direct = true; NeedsMutable = false; Defs = (fun _ -> []); Read = fun _ -> "x" }
      { Name = "fun"; Direct = false; NeedsMutable = false; Defs = (fun k -> [ sprintf "let f%d () = x" k ]); Read = fun k -> sprintf "f%d ()" k }
      { Name = "fun-twice"; Direct = false; NeedsMutable = false; Defs = (fun k -> [ sprintf "let f%d () = x" k ]); Read = fun k -> sprintf "(f%d () + f%d ())" k k }
      { Name = "alias"; Direct = false; NeedsMutable = false; Defs = (fun k -> [ sprintf "let a%d = x" k ]); Read = fun k -> sprintf "a%d" k }
      { Name = "rec"; Direct = false; NeedsMutable = false
        Defs = (fun k -> [ sprintf "let rec r%d i = if i = 0 then x else r%d (i - 1)" k k ]); Read = fun k -> sprintf "r%d 2" k }
      { Name = "lambda-value"; Direct = false; NeedsMutable = false; Defs = (fun k -> [ sprintf "let g%d = fun () -> x" k ]); Read = fun k -> sprintf "g%d ()" k }
      { Name = "setter"; Direct = false; NeedsMutable = true
        Defs = (fun k -> [ sprintf "let s%d () = x <- x + 100; x" k ]); Read = fun k -> sprintf "s%d ()" k } ]

/// Where the block sits: the member's last expression, or a once-called local function's.
let placements =
    [ "top", (fun (block: string) -> [ block ])
      "local-fun", (fun block -> [ "let run () : string ="; "    " + block; "run ()" ]) ]

let fits (b: Binding) (r: Reach) = not r.NeedsMutable || b.IsMutable

type Case = { Id: string; Lines: string list; Params: string }

let block (reads: string list) =
    sprintf "dlr { return o?Echo(%s) }" (String.Join(", ", reads))

let make id (outer: (Binding * Reach) option) (inner: Binding * Reach) distractor placement =
    let ib, ir = inner
    let reads = [ match outer with Some(_, r) -> yield r.Read 1 | None -> ()
                  yield ir.Read 2
                  if distractor then yield "d" ]
    let place = placements |> List.find (fst >> (=) placement) |> snd
    let tail =
        [ if distractor then yield "let d = [ 1 ] |> List.map (fun x -> x + 1000) |> List.head" ]
        @ place (block reads)
    let innerLines = ib.Wrap 2 (ir.Defs 2 @ tail)
    let lines =
        match outer with
        | Some(ob, orr) -> ob.Wrap 1 (orr.Defs 1 @ innerLines)
        | None -> innerLines
    let isParam = match outer with Some(ob, _) -> ob.IsParam | None -> ib.IsParam
    { Id = id; Lines = lines; Params = if isParam then "(o: obj) (seed: int) (seed2: int) (x: int)" else "(o: obj) (seed: int) (seed2: int)" }

let cases =
    [ // One `x`, every way in, with and without an unrelated lambda's `x`.
      for b in bindings do
        for r in reaches do
          if fits b r then
            for p, _ in placements do
              for d in [ false; true ] ->
                make (sprintf "one_%s_%s_%s%s" b.Name r.Name p (if d then "_distractor" else "")) None (b, r) d p
      // Two `x`s: the outer reached indirectly (the inner shadows it), the inner any way.
      for ob in bindings do
        for orr in reaches do
          if not orr.Direct && fits ob orr then
            for ib in bindings do
              if not ib.IsParam then
                for ir in reaches do
                  if fits ib ir then
                    for p, _ in placements ->
                      make (sprintf "two_%s_%s__%s_%s_%s" ob.Name orr.Name ib.Name ir.Name p) (Some(ob, orr)) (ib, ir) false p ]

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
          yield "#nowarn \"1182\""
          yield ""
          for c in chunk do
            yield "[<ReflectedDefinition>]"
            yield sprintf "let %s %s : string =" (identifier c.Id) c.Params
            yield! indent c.Lines
            yield "" ]
    File.WriteAllLines(Path.Combine(dir, sprintf "Cases%03d.g.fs" i), body))
printfn "%d cases in %d files" cases.Length ((cases.Length + perFile - 1) / perFile)
