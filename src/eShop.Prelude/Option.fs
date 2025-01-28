namespace eShop.Prelude

open eShop.ConstrainedTypes

[<RequireQualifiedAccess>]
module Option =
    let ofList x =
        match x with
        | [] -> None
        | head :: tail -> tail |> NonEmptyList.create head |> Some

    let ofMap x =
        match x |> Map.count with
        | 0 -> None
        | _ -> Some x
