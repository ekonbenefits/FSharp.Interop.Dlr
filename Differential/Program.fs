/// Runs every generated case twice (the second call takes the cached site) and prints, per case,
/// its results and the fields of its block's container: `id<TAB>first<TAB>second<TAB>fields`.
module Differential.Program

open System
open System.Reflection
open Differential.Support

let private outcome (f: unit -> obj) =
    try string (f ())
    with
    | :? TargetInvocationException as e when not (isNull e.InnerException) ->
        let inner = e.InnerException
        sprintf "%s: %s" (inner.GetType().Name) (inner.Message.Split('\n').[0])

[<EntryPoint>]
let main _ =
    let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let assembly = Assembly.GetExecutingAssembly()
    let target = box (Echo())
    let types = assembly.GetTypes()
    for m in types |> Array.filter (fun t -> t.FullName.StartsWith "Differential.Cases") |> Array.sortBy (fun t -> t.FullName) do
        for mi in m.GetMethods(BindingFlags.Public ||| BindingFlags.Static) |> Array.sortBy (fun mi -> mi.MetadataToken) do
            // o, seed, seed2, then the case's own `x`: 5, an S of 5, or a tuple (5, 70), which F#
            // compiles as two parameters.
            let args : obj[] =
                let own = mi.GetParameters() |> Array.skip 3 |> Array.map (fun p -> p.ParameterType)
                let x : obj[] =
                    match own with
                    | [||] -> [||]
                    | [| t |] when t = typeof<S> -> [| S 5 |]
                    | [| _ |] -> [| 5 |]
                    | _ -> [| 5; 70 |]
                Array.append [| target; 7; 11 |] x
            let run () =
                Ticks.Reset()
                outcome (fun () -> mi.Invoke(null, args))
            let first = run ()
            let second = run ()
            let fields =
                types
                |> Array.filter (fun t -> t.Name.StartsWith(mi.Name + "@") && t.GetFields(flags).Length > 0)
                |> Array.map (fun t -> String.Join(",", t.GetFields(flags) |> Array.map (fun f -> f.Name)))
                |> fun a -> String.Join(" ", a)
            printfn "%s\t%s\t%s\t%s" mi.Name first second fields
    0
