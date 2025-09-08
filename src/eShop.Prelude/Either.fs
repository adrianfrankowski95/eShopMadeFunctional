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

    let inline either
        ([<InlineIfLambda>] leftMapper: 'a -> 'c)
        ([<InlineIfLambda>] rightMapper: 'b -> 'd)
        (either: Either<'a, 'b>)
        =
        match either with
        | Left left -> left |> leftMapper |> Left
        | Right right -> right |> rightMapper |> Right

    let inline collapse (either: Either<'a, 'a>) =
        match either with
        | Left left -> left
        | Right right -> right
