namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Map =
    let inline mapValues ([<InlineIfLambda>] mapping) map =
        map |> Map.map (fun _ k -> k |> mapping)

    let rec removeKeys map (keysToRemove: seq<_>) =
        match keysToRemove |> Seq.toList with
        | head :: tail -> removeKeys (map |> Map.remove head) tail
        | [] -> map
