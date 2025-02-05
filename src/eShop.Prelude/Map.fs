namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Map =
    let inline mapValues mapping map =
        map |> Map.map (fun _ k -> k |> mapping)

    let rec inline removeKeys map (keysToRemove: seq<_>) =
        match keysToRemove |> Seq.toList with
        | head :: tail -> removeKeys (map |> Map.remove head) tail
        | [] -> map
