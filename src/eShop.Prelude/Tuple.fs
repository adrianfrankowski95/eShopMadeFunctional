namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Tuple =
    let inline mapFst f (x, y) = (x |> f, y)

    let inline mapSnd f (x, y) = (x, y |> f)
