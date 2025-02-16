namespace eShop.Prelude

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

module Operators =
    let (>=>) f g = f >> Result.bind g
