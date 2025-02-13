namespace eShop.ConstrainedTypes

open FsToolkit.ErrorHandling

type NonEmptyMap<'k, 'v when 'k: comparison> = private NonEmptyMap of Map<'k, 'v>

[<RequireQualifiedAccess>]
module NonEmptyMap =
    let create key value =
        Map.empty |> Map.add key value |> NonEmptyMap

    let ofMap map =
        match map |> Map.count with
        | 0 -> "Invalid NonEmptyMap: Provided map is empty" |> Error
        | _ -> map |> NonEmptyMap |> Ok

    let ofNonEmptyList (((key, value), tail): NonEmptyList<'k * 'v>) =
        tail |> Map.ofList |> Map.add key value |> NonEmptyMap

    let toMap (NonEmptyMap map) = map

    let toList map = map |> toMap |> Map.toList

    let map mapping map =
        map |> toMap |> Map.map mapping |> NonEmptyMap

    let mapValues mapping map =
        map |> toMap |> Map.map (fun _ v -> v |> mapping) |> NonEmptyMap

    let count map = map |> toMap |> Map.count

    let add key value map =
        map |> toMap |> Map.add key value |> NonEmptyMap

    let traverseResultA f (NonEmptyMap xs) =
        xs
        |> Map.toList
        |> List.traverseResultA f
        |> Result.map (Map.ofList >> NonEmptyMap)

[<AutoOpen>]
module ActivePatterns =
    let (|NonEmptyMap|) = NonEmptyMap.toMap
