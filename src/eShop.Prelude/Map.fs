namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Map =
    let inline mapValues ([<InlineIfLambda>] mapping) map =
        map |> Map.map (fun _ k -> k |> mapping)

    let rec removeKeys (keysToRemove: seq<_>) map =
        match keysToRemove |> Seq.toList with
        | head :: tail -> map |> Map.remove head |> removeKeys tail
        | [] -> map
