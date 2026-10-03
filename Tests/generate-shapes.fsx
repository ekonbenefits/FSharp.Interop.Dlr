// Generates Tests/Shapes.fs (#162): the shape corpus. Every quotation node kind F# produces,
// as a body over an int `x`, in every context a block can hold it; each cell runs the body in
// `dlr { }` (its input read through a marker) and as plain F#, and the two must agree.
//   dotnet fsi Tests/generate-shapes.fsx
// A cell the translator does not support is listed in `unsupported` with the reason, and its
// test pins the DlrTranslationException instead; every other cell must pass. "In a for body" is
// the builder's own `for`; the raw loop nodes are the WhileLoop / ForIntegerRangeLoop bodies.
// Not rows: `for … downto` (no quotation can hold it) and a stepped range (a while over an
// enumerator, so the WhileLoop row).
open System
open System.IO

/// A body: its name (the node kind it exercises), its result type, and the expression over `x`.
type Body = { Name: string; Type: string; Expr: string }

let bodies =
    [ { Name = "Value"; Type = "int"; Expr = "42 + x - x" }
      { Name = "Var"; Type = "int"; Expr = "x" }
      { Name = "Call (static)"; Type = "int"; Expr = "max x 1" }
      { Name = "Call (instance)"; Type = "string"; Expr = "x.ToString()" }
      { Name = "Let"; Type = "int"; Expr = "let y = x + 1 in y * 2" }
      { Name = "LetRecursive"; Type = "int"; Expr = "let rec sum n = if n <= 0 then 0 else n + sum (n - 1) in sum x" }
      { Name = "Lambda / Application"; Type = "int"; Expr = "(fun y -> y + x) 1" }
      { Name = "Lambda returning a lambda"; Type = "int"; Expr = "(fun a -> fun b -> a * b) x 2" }
      { Name = "IfThenElse"; Type = "int"; Expr = "if x > 2 then 1 else 0" }
      { Name = "VarSet / Sequential"; Type = "int"; Expr = "(let mutable m = x in m <- m + 1; m)" }
      { Name = "WhileLoop"; Type = "int"; Expr = "(let mutable i = 0 in (while i < x do i <- i + 1); i)" }
      { Name = "ForIntegerRangeLoop"; Type = "int"; Expr = "(let mutable s = 0 in (for i in 1 .. x do s <- s + i); s)" }
      { Name = "TryWith"; Type = "int"; Expr = "(try x / (x - x) with _ -> -x)" }
      { Name = "TryWith (type test, unmatched)"; Type = "int"; Expr = "(try (try x / (x - x) with :? ArgumentException -> 0) with :? DivideByZeroException -> -x)" }
      { Name = "TryFinally"; Type = "int"; Expr = "(let mutable f = 0 in (try f <- x finally f <- f * 2); f)" }
      { Name = "Use"; Type = "int"; Expr = "(use s = new IO.MemoryStream() in int s.Length + x)" }
      { Name = "NewTuple / TupleGet"; Type = "int"; Expr = "(let (a, b) = (x, x + 1) in a + b)" }
      { Name = "NewStructTuple"; Type = "int"; Expr = "(let struct (a, b) = struct (x, 2) in a * b)" }
      { Name = "NewRecord (anonymous)"; Type = "int"; Expr = "{| A = x; B = 1 |}.A + 1" }
      { Name = "NewUnionCase / UnionCaseTest (option)"; Type = "int"; Expr = "(match Some x with Some v -> v | None -> 0)" }
      { Name = "NewUnionCase / UnionCaseTest (list)"; Type = "int"; Expr = "(match [ x; 1 ] with h :: _ -> h | [] -> 0)" }
      { Name = "NewArray / array index"; Type = "int"; Expr = "[| x; x * 2 |].[1]" }
      { Name = "NewObject / PropertyGet"; Type = "string"; Expr = "Text.StringBuilder().Append(x).ToString()" }
      { Name = "PropertySet"; Type = "int"; Expr = "(let r = ref x in r.Value <- r.Value + 1; r.Value)" }
      { Name = "Void call as a statement"; Type = "int"; Expr = "(let l = ResizeArray<int>() in l.Add x; l.Count)" }
      { Name = "FieldGet"; Type = "int"; Expr = "int (Numerics.Vector2(float32 x, 2.0f).X)" }
      { Name = "FieldSet on a mutable struct"; Type = "int"; Expr = "(let mutable v = Numerics.Vector2(1.0f, 2.0f) in v.X <- float32 x; int v.X)" }
      { Name = "PropertySet on a mutable struct"; Type = "int"; Expr = "(let mutable s = Tests.CSharp.MutableSlot() in s.Number <- x; s.Number)" }
      { Name = "Mutating method on a mutable struct"; Type = "int"; Expr = "(let mutable s = Tests.CSharp.MutableSlot() in s.Store(Func<int, int>(fun y -> y + x)); s.Property.Invoke 1)" }
      { Name = "Field set through a struct field"; Type = "int"; Expr = "(let mutable v = Tests.CSharp.OuterPoint() in v.Inner.X <- x; v.Inner.X)" }
      { Name = "Argument mutating the struct it is passed to"; Type = "int"; Expr = "(let mutable v = Tests.CSharp.OuterPoint() in v.Total <- v.Next() + v.Next() + x; v.N * 100 + v.Total)" }
      { Name = "Coerce / TypeTest"; Type = "int"; Expr = "(match box x with :? int as i -> i | _ -> 0)" }
      { Name = "DefaultValue"; Type = "int"; Expr = "Unchecked.defaultof<int> + x" }
      { Name = "NewDelegate"; Type = "int"; Expr = "Func<int, int>(fun y -> y + x).Invoke 1" }
      { Name = "NewDelegate (parameterless)"; Type = "int"; Expr = "Func<int>(fun () -> x).Invoke()" }
      { Name = "Raise caught"; Type = "int"; Expr = "(try raise (InvalidOperationException()) with _ -> x)" }
      { Name = "List comprehension"; Type = "int"; Expr = "[ for i in 1 .. x -> i * i ] |> List.sum" }
      { Name = "Seq expression"; Type = "int"; Expr = "seq { yield x; yield 1 } |> Seq.sum" }
      { Name = "String interpolation"; Type = "string"; Expr = "$\"n={x}\"" }
      { Name = "Lazy"; Type = "int"; Expr = "(lazy (x + 1)).Value" }
      { Name = "Pipe and composition"; Type = "int"; Expr = "x |> ((+) 1 >> (*) 2)" } ]

/// A context: how a block holds the body. `{B}` is the body, `{T}` its type; the input is
/// `x0` (read through a marker before), or `(o?Count : int)` inline in the marker variant.
type Context = { Name: string; Code: string }

let contexts =
    [ { Name = "as the block's value"; Code = "let x = x0 in return ({B})" }
      { Name = "under a lambda"; Code = "return ([ x0 ] |> List.map (fun x -> {B})).Head" }
      { Name = "in a delegate literal"; Code = "return Func<int, {T}>(fun x -> {B}).Invoke x0" }
      { Name = "in a for body"; Code = "let mutable r = Unchecked.defaultof<{T}>\n            for x in [ x0 ] do\n                r <- ({B})\n            return r" }
      { Name = "in a try body"; Code = "try\n                let x = x0 in return ({B})\n            with _ -> return Unchecked.defaultof<{T}>" }
      { Name = "in a nested dlr"; Code = "return (dlr { let x = x0 in return ({B}) } : {T})" } ]

/// Cells the translator does not support, with the reason (pinned as a translation error).
let unsupported : Map<string * string, string> = Map.empty

let identifier (s: string) = s.Replace("`", "'")

/// Where the body's input comes from: read through a marker before the body (`x0`), or inside
/// the body itself, wherever it sits — through a member read, a computed name (a per-key site),
/// or an out argument (a byref site).
type Input = { Suffix: string; Inside: string option }

let inputs =
    [ { Suffix = ""; Inside = None }
      { Suffix = ", marker inside"; Inside = Some "let x: int = o?Count in" }
      { Suffix = ", computed name inside"; Inside = Some "let x: int = (?) o name in" }
      { Suffix = ", out inside"; Inside = Some "let (_: bool), (x: int) = d?TryGetValue(\"k\", Dlr.out) in" } ]

let cell (b: Body) (c: Context) (input: Input) =
    let code =
        match input.Inside with
        | Some read ->
            c.Code.Replace("let x = x0 in ", "").Replace("fun x ->", "fun (_: int) ->").Replace("for x in", "for _ in")
                  .Replace("{B}", sprintf "%s %s" read b.Expr)
        | None -> c.Code.Replace("{B}", b.Expr)
    let code = code.Replace("{T}", b.Type)
    let name = sprintf "%s — %s%s" b.Name c.Name input.Suffix
    String.Join("\n",
        [ "[<Fact>]"
          sprintf "let ``%s`` () =" (identifier name)
          "    let w = Widget()"
          (if code.Contains "o?" || code.Contains "(?) o" || code.Contains "x0" then "    let o = box w" else "")
          (if code.Contains "name" then "    let name = \"Count\"" else "")
          (if code.Contains "d?" then "    let d = box (Collections.Generic.Dictionary<string, int>(dict [ \"k\", 3 ]))" else "")
          sprintf "    let expected: %s = (let x = w.Count in %s)" b.Type b.Expr
          sprintf "    let actual: %s =" b.Type
          "        dlr {"
          (if code.Contains "x0" then "            let x0: int = o?Count" else "")
          sprintf "            %s" code
          "        }"
          "    actual |> should equal expected" ] |> List.filter (fun l -> l <> "")) + "\n"

let header = """// Generated by generate-shapes.fsx (#162) — do not edit; change the generator and rerun it.
// The shape corpus: every body in every context, run in dlr { } and as plain F#.
[<ReflectedDefinition>]
module Tests.Shapes

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

"""

let source =
    header
    + String.Join("\n", [ for b in bodies do for c in contexts do for input in inputs -> cell b c input ])

File.WriteAllText(Path.Combine(__SOURCE_DIRECTORY__, "Shapes.fs"), source)
printfn "Shapes.fs: %d bodies × %d contexts × %d inputs = %d cells" bodies.Length contexts.Length inputs.Length (bodies.Length * contexts.Length * inputs.Length)
