namespace eShop.Prelude

open FsToolkit.ErrorHandling

type AsyncResult<'ok, 'err> = Result<'ok, 'err> Async

[<RequireQualifiedAccess>]
module AsyncResult =
    let collapse x = x |> Async.map Result.collapse

    let inline teeAsync ([<InlineIfLambda>] f) x =
        async {
            let! result = x

            return!
                match result with
                | Ok ok ->
                    async {
                        do! f ok
                        return ok |> Ok
                    }
                | Error err -> err |> AsyncResult.error
        }

    let inline teeErrorAsync ([<InlineIfLambda>] f) x =
        async {
            let! result = x

            return!
                match result with
                | Ok ok -> ok |> AsyncResult.ok
                | Error err ->
                    async {
                        do! f err
                        return err |> Error
                    }
        }

    let inline teeUnitAsync ([<InlineIfLambda>] f) x =
        async {
            let! result = x

            do! f ()

            return result
        }
