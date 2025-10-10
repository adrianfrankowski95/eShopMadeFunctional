namespace eShop.Prelude

[<Struct>]
type Either<'left, 'right> =
    | Left of left: 'left
    | Right of right: 'right

[<RequireQualifiedAccess>]
module Either =
    let inline bimap ([<InlineIfLambda>] f) ([<InlineIfLambda>] g) (either: Either<'a, 'b>) =
        match either with
        | Left left -> left |> f |> Left
        | Right right -> right |> g |> Right

    let inline mapLeft ([<InlineIfLambda>] f) either = bimap f id either

    let inline mapRight ([<InlineIfLambda>] f) either = bimap id f either

    let inline either
        ([<InlineIfLambda>] f)
        ([<InlineIfLambda>] g)
        (either: Either<'a, 'b>)
        =
        match either with
        | Left left -> left |> f
        | Right right -> right |> g

    let inline collapse (either: Either<'a, 'a>) =
        match either with
        | Left left -> left
        | Right right -> right
