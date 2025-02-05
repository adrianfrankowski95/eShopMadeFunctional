namespace eShop.Prelude

[<RequireQualifiedAccess>]
module Option =
    let ofList x =
        match x with
        | [] -> None
        | head :: tail -> (tail, head) |> Some

    let ofMap x =
        match x |> Map.count with
        | 0 -> None
        | _ -> Some x
