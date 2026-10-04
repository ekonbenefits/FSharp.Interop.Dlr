namespace FSharp.Interop.Dlr

open System
open System.Linq.Expressions
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.ExprShape
open FSharp.Reflection
open Microsoft.FSharp.Linq.RuntimeHelpers
open System.Runtime.CompilerServices

module internal SiteHoisting =
    open TranslatePatterns

    /// The call sites of a compiled block hoisted into locals of the lambda that uses them.
    /// `LambdaExpression.Compile` keeps a reference-type constant in its closure's `Constants`
    /// array and re-reads and casts it at every use — two per site call (`site.Target` and the
    /// `site` argument). This visitor gives each lambda — the block's own and every nested one
    /// (loop and try bodies, user lambdas) — a `Block` binding the sites its body uses directly to
    /// variables assigned once at entry, so a use is a local read. Per lambda, not at the block's
    /// entry: a variable captured by a nested lambda would be a `StrongBox` read, no better than
    /// the constant. Done on the LINQ tree rather than as quotation `Let`s, which FSharp.Core
    /// before 10.1 converts to nested lambda invocations (50× slower, measured).
    type SiteHoister() =
        inherit ExpressionVisitor()
        let mutable current : System.Collections.Generic.Dictionary<CallSite, ParameterExpression> = null

        /// A placeholder's argument, boxed to `obj` in its array, back at the parameter type it was
        /// boxed from (the box dropped when it is that type already).
        static let unboxed (e: Expression) (wanted: Type) =
            match e with
            | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert && u.Type = typeof<obj> && u.Operand.Type = wanted -> u.Operand
            | e when e.Type = wanted -> e
            | e -> Expression.Convert(e, wanted) :> Expression

        /// The site's `Target` delegate, the site cast from the placeholder's `CallSite` to its type.
        static let siteTarget (site: Expression) (siteDelegate: Type) =
            Expression.Field(Expression.Convert(site, typedefof<CallSite<_>>.MakeGenericType siteDelegate), "Target")

        override this.VisitLambda<'T>(node: Expression<'T>) : Expression =
            let saved = current
            current <- System.Collections.Generic.Dictionary(HashIdentity.Reference)
            let body = this.Visit node.Body
            let mine = current
            current <- saved
            if mine.Count = 0 then node.Update(body, node.Parameters) :> Expression
            else
                let assigns = [ for KeyValue(site, var) in mine -> Expression.Assign(var, Expression.Constant(site, var.Type)) :> Expression ]
                let block = Expression.Block(body.Type, mine.Values, assigns @ [ body ])
                node.Update(block, node.Parameters) :> Expression

        override _.VisitConstant(node: ConstantExpression) : Expression =
            match node.Value with
            | :? CallSite as site when not (isNull current) ->
                match current.TryGetValue site with
                | true, var -> var :> Expression
                | _ ->
                    let var = Expression.Variable(node.Type, "site")
                    current.[site] <- var
                    var :> Expression
            | _ -> node :> Expression

        /// A wide site's placeholder (`Binders.WideSite`) becomes the typed `Invoke` on the site's
        /// `Target`: the delegate type from the placeholder's constant, each argument unboxed
        /// back to the parameter type it was boxed from.
        override this.VisitMethodCall(node: MethodCallExpression) : Expression =
            if node.Method.DeclaringType = typeof<Binders.WideSite> then
                let site = this.Visit node.Arguments.[0]
                let siteDelegate = (node.Arguments.[1] :?> ConstantExpression).Value :?> Type
                let parameters = siteDelegate.GetMethod("Invoke").GetParameters()
                let elements = (node.Arguments.[2] :?> NewArrayExpression).Expressions
                let args = [ for i in 0 .. elements.Count - 1 -> unboxed (this.Visit elements.[i]) parameters.[i + 1].ParameterType ]
                let invoke = Expression.Invoke(siteTarget site siteDelegate, (Expression.Convert(site, typeof<CallSite>) :> Expression) :: args)
                if node.Method.Name = "InvokeVoid" then invoke :> Expression
                elif invoke.Type = typeof<obj> then invoke :> Expression
                else Expression.Convert(invoke, typeof<obj>) :> Expression
            // A byref site's placeholder (`Binders.ByRefSite`) becomes the typed `Invoke` over a
            // variable per byref parameter (a ref's value in, the default for an out), which LINQ
            // writes back; then the holder `'H` — result, then each byref's value, typed — the
            // translator unpacks. Required on wasm, where DynamicInvoke does not write byrefs back.
            elif node.Method.DeclaringType = typeof<Binders.ByRefSite> then
                let site = this.Visit node.Arguments.[0]
                let siteDelegate = (node.Arguments.[1] :?> ConstantExpression).Value :?> Type
                let invokeMethod = siteDelegate.GetMethod("Invoke")
                let parameters = invokeMethod.GetParameters()
                let elements = (node.Arguments.[2] :?> NewArrayExpression).Expressions
                // Out positions are explicit: in a per-key template an out's value in is a parameter,
                // not the null constant, and an emitted delegate's parameter carries no [Out].
                let outs = (node.Arguments.[4] :?> ConstantExpression).Value :?> int[]
                let sameAs = (node.Arguments.[5] :?> ConstantExpression).Value :?> int[]
                let tempAt = System.Collections.Generic.Dictionary<int, ParameterExpression>()
                let temps = ResizeArray<ParameterExpression>()
                let inits = ResizeArray<Expression>()
                let args =
                    [ for i in 0 .. elements.Count - 1 ->
                        let p = parameters.[i + 1]
                        let e = this.Visit elements.[i]
                        if p.ParameterType.IsByRef && sameAs.[i] >= 0 then
                            // The same variable as an earlier ref: the same storage, as C# passes it.
                            tempAt.[i] <- tempAt.[sameAs.[i]]
                            tempAt.[i] :> Expression
                        elif p.ParameterType.IsByRef then
                            let t = p.ParameterType.GetElementType()
                            let temp = Expression.Variable(t, "byRef")
                            temps.Add temp
                            tempAt.[i] <- temp
                            let initial = if Array.contains i outs then Expression.Default(t) :> Expression else unboxed e t
                            inits.Add(Expression.Assign(temp, initial))
                            temp :> Expression
                        else unboxed e p.ParameterType ]
                let call = Expression.Call(siteTarget site siteDelegate, invokeMethod, (Expression.Convert(site, typeof<CallSite>) :> Expression) :: args)
                let result, callStep =
                    if invokeMethod.ReturnType = typeof<Void> then (Expression.Constant(null, typeof<obj>) :> Expression), (call :> Expression)
                    else
                        let r = Expression.Variable(invokeMethod.ReturnType, "result")
                        temps.Add r
                        (Expression.Convert(r, typeof<obj>) :> Expression), (Expression.Assign(r, call) :> Expression)
                // The holder `node.Type` (Binders.byRefHolderType).
                let values = Tuples.newExpression node.Type (result :: [ for i in 0 .. elements.Count - 1 do if parameters.[i + 1].ParameterType.IsByRef then yield (tempAt.[i] :> Expression) ])
                Expression.Block(node.Type, temps, List.ofSeq inits @ [ callStep; values ]) :> Expression
            // An in-place struct operation's write-back (`Binders.InPlace`): in a finally.
            elif node.Method.DeclaringType = typeof<Binders.InPlace> then
                Expression.TryFinally(this.Visit node.Arguments.[0], this.Visit node.Arguments.[1]) :> Expression
            else base.VisitMethodCall node

    let onWasm =
        string System.Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm"   // no Architecture.Wasm on netstandard2.0
