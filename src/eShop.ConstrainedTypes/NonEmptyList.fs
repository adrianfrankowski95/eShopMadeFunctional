namespace eShop.ConstrainedTypes

open FsToolkit.ErrorHandling

type NonEmptyList<'t> = 't * List<'t>

[<RequireQualifiedAccess>]
module NonEmptyList =
    let create (head: 't) (tail: List<'t>) = (head, tail) |> NonEmptyList

    let ofList list =
        match list with
        | [] ->
            "Invalid NonEmptyList: Provided list is empty"
            |> Error
        | head :: tail -> create head tail |> Ok

    let toList = List.Cons

    let map mapping ((head, tail): NonEmptyList<'t>) =
        (mapping head, List.map mapping tail)
        |> NonEmptyList

    let length ((_, tail): NonEmptyList<'t>) = tail |> List.length |> (+) 1

    let sequence sequencer mapper =
        toList
        >> sequencer
        >> mapper (ofList >> Result.valueOr failwith)

    let traverseResultA f =
        sequence (List.traverseResultA f) Result.map

    let traverseResultM f =
        sequence (List.traverseResultM f) Result.map

    let traverseOptionM f =
        sequence (List.traverseOptionM f) Option.map

    let traverseAsyncResultA f =
        sequence (List.traverseAsyncResultA f) AsyncResult.map

    let traverseAsyncResultM f =
        sequence (List.traverseAsyncResultM f) AsyncResult.map

    let traverseAsyncOptionM f =
        sequence (List.traverseAsyncOptionM f) AsyncOption.map