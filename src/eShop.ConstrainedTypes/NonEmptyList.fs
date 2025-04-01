namespace eShop.ConstrainedTypes

open FsToolkit.ErrorHandling

type NonEmptyList<'t> = 't * List<'t>

[<RequireQualifiedAccess>]
module NonEmptyList =
    let create (head: 't) (tail: List<'t>) = (head, tail) |> NonEmptyList

    let ofList list =
        match list with
        | [] -> "Invalid NonEmptyList: Provided list is empty" |> Error
        | head :: tail -> create head tail |> Ok

    let toList = List.Cons

    let inline map ([<InlineIfLambda>] mapping) ((head, tail): NonEmptyList<'t>) =
        (mapping head, List.map mapping tail) |> NonEmptyList

    let inline length ((_, tail): NonEmptyList<'t>) = tail |> List.length |> (+) 1

    let inline contains value ((head, tail): NonEmptyList<'t>) =
        head = value || (tail |> List.contains value)

    let inline sequence ([<InlineIfLambda>] sequencer) ([<InlineIfLambda>] mapper) =
        toList >> sequencer >> mapper (ofList >> Result.valueOr failwith)

    let inline traverseResultA ([<InlineIfLambda>] f) =
        sequence (List.traverseResultA f) Result.map

    let inline traverseResultM ([<InlineIfLambda>] f) =
        sequence (List.traverseResultM f) Result.map

    let inline traverseOptionM ([<InlineIfLambda>] f) =
        sequence (List.traverseOptionM f) Option.map

    let inline traverseAsyncResultA ([<InlineIfLambda>] f) =
        sequence (List.traverseAsyncResultA f) AsyncResult.map

    let inline traverseAsyncResultM ([<InlineIfLambda>] f) =
        sequence (List.traverseAsyncResultM f) AsyncResult.map

    let inline traverseAsyncOptionM ([<InlineIfLambda>] f) =
        sequence (List.traverseAsyncOptionM f) AsyncOption.map
