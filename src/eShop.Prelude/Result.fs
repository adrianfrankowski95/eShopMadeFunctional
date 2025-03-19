namespace eShop.Prelude

open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Result =
    let inline collapse x =
        match x with
        | Ok x -> x
        | Error x -> x

    let inline extractList results =
        results
        |> Seq.fold
            (fun (oks, errors) ->
                function
                | Ok ok -> oks @ [ ok ], errors
                | Error err -> oks, errors @ [ err ])
            ([], [])

    let inline requireSingleOrEmpty x =
        match x |> Seq.length with
        | 1 -> x |> Seq.head |> ResultOption.retn
        | 0 -> None |> Ok
        | length -> $"Expected single item, but received %d{length}" |> Error