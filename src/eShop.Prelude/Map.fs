namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Map =
    let rec inline removeKeys (keysToRemove: seq<_>) map =
        match keysToRemove |> Seq.toList with
        | head :: tail -> map |> Map.remove head |> removeKeys tail
        | [] -> map
