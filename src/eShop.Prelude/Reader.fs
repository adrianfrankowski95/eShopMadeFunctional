namespace eShop.Prelude

type Reader<'env, 'a> = 'env -> 'a

[<RequireQualifiedAccess>]
module Reader =
    let inline retn x = fun _ -> x

    let inline run (env: 'env) (x: Reader<'env, 'a>) : 'a = x env

    let inline bind ([<InlineIfLambda>] f: 'a -> Reader<'env, 'b>) (x: Reader<'env, 'a>) : Reader<'env, 'b> =
        fun (env: 'env) ->
            let a = x env
            f a env

    let inline map ([<InlineIfLambda>] f: 'a -> 'b) (x: Reader<'env, 'a>) : Reader<'env, 'b> = bind (f >> retn) x

    let inline apply (f: Reader<'env, 'a -> 'b>) (a: Reader<'env, 'a>) : Reader<'env, 'b> = bind (fun f -> map f a) f

    let inline combine (a: Reader<'env, 'a>) (b: Reader<'env, 'b>) : Reader<'env, 'b> = bind (fun _ -> b) a

    let inline ignore x : Reader<'a, unit> = map (fun _ -> ()) x

    let ask: Reader<'env, 'env> = id

    let inline asks (f: 'env -> 'a) = f

    let inline local ([<InlineIfLambda>] f: 'env1 -> 'env2) (x: Reader<'env2, 'a>) : Reader<'env1, 'a> = f >> x

type ReaderBuilder() =
    member _.Return(x) = Reader.retn x
    member _.Bind(x, f) = Reader.bind f x
    member _.ReturnFrom(x) = x
    member _.Zero() = Reader.retn ()
    member _.Combine(x, y) = Reader.combine x y
    member _.Delay(f) = f ()
    member _.Run(x) = x

[<AutoOpen>]
module CE =
    let reader = ReaderBuilder()
