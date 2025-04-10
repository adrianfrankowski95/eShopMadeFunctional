namespace eShop.Prelude

[<Struct>]
type Either<'left, 'right> =
    | Left of left: 'left
    | Right of right: 'right

[<RequireQualifiedAccess>]
module Either =
    let inline mapLeft ([<InlineIfLambda>] mapper) either =
        match either with
        | Left x -> x |> mapper |> Left
        | Right x -> x |> Left

    let inline mapRight ([<InlineIfLambda>] mapper: 'b -> 'c) (either: Either<'a, 'b>) : Either<'a, 'c> =
        match either with
        | Right x -> x |> mapper |> Right
        | Left x -> x |> Left
