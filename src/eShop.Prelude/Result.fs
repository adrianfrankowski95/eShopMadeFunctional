namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Result =
    let inline collapse x =
        match x with
        | Ok x -> x
        | Error x -> x
