namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Tuple =
    let inline mapFst ([<InlineIfLambda>] f) (x, y) = (x |> f, y)

    let inline mapSnd ([<InlineIfLambda>] f) (x, y) = (x, y |> f)
