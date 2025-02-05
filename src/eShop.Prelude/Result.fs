namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Result =
    let inline collapse x =
        match x with
        | Ok x -> x
        | Error x -> x

module Operators =
    let (>=>) f g = f >> Result.bind g
