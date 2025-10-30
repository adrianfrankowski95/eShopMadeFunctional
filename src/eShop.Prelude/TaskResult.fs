namespace eShop.Prelude

open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module TaskResult =
    let collapse x = x |> Task.map Result.collapse

    let inline catch ([<InlineIfLambda>] f) (t: unit -> #Task) =
        task {
            try
                let! result = t()
                return result |> Ok
            with e ->
                return e |> f |> Error
        }
    
    let inline teeAsync ([<InlineIfLambda>] f: 'a -> #Task) x =
        task {
            let! result = x

            return!
                match result with
                | Ok ok ->
                    task {
                        do! f ok
                        return ok |> Ok
                    }
                | Error err -> err |> TaskResult.error
        }

    let inline teeErrorAsync ([<InlineIfLambda>] f: 'a -> #Task) x =
        task {
            let! result = x

            return!
                match result with
                | Ok ok -> ok |> TaskResult.ok
                | Error err ->
                    task {
                        do! f err
                        return err |> Error
                    }
        }

    let inline teeAnyAsync ([<InlineIfLambda>] f: unit -> #Task) x =
        task {
            let! result = x

            do! f ()

            return result
        }
