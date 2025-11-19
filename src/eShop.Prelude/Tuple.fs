namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Tuple =
    let inline mapFst ([<InlineIfLambda>] f) (x, y) = (x |> f, y)

    let inline mapSnd ([<InlineIfLambda>] f) (x, y) = (x, y |> f)
    
    let inline mapBoth ([<InlineIfLambda>] f) (x, y) = (x |> f, y |> f)
    
    let inline swap (x, y) = (y, x)
    
    let inline biMap ([<InlineIfLambda>] f) ([<InlineIfLambda>] g) (x, y) = (x |> f, y |> g)
