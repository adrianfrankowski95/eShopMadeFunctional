namespace eShop.Prelude

module Tuple =
    let inline mapFst f (x, y) = (x |> f, y)
    
    let inline mapSnd f (x, y) = (x, y |> f)