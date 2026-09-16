namespace FSharp.Interop.DLR

open System

type Named<'T> = Named of 'T

[<AutoOpen>]
module Operators =
    let private outside (name: string) : 'T =
        raise (InvalidOperationException(sprintf "'%s' is only meaningful inside dlr { }; it is inspected as a quotation, never executed." name))

    let ( ? ) (target: obj) (name: string) : 'TResult = ignore (target, name); outside "?"
    let ( ?<- ) (target: obj) (name: string) (value: 'TValue) : unit = ignore (target, name, value); outside "?<-"
    let ( !? ) (target: obj) : 'TResult = ignore target; outside "!?"
    let getIndex (target: obj) (indexes: 'TIndex) : 'TResult = ignore (target, indexes); outside "getIndex"
    let setIndex (target: obj) (indexes: 'TIndex) (value: 'TValue) : unit = ignore (target, indexes, value); outside "setIndex"

    let ( ?%? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?%?"
    let ( ?*? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?*?"
    let ( ?+? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?+?"
    let ( ?-? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?-?"
    let ( ?/? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?/?"
    let ( ?&&&? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?&&&?"
    let ( ?|||? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?|||?"
    let ( ?^^^? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?^^^?"
    let ( ?<<<? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?<<<?"
    let ( ?>>>? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?>>>?"
    let ( ?<=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<=?"
    let ( ?<>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<>?"
    let ( ?<? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<?"
    let ( ?=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?=?"
    let ( ?>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>?"
    let ( ?>=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>=?"
