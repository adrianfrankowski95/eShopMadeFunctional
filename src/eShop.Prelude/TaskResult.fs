namespace eShop.Prelude

open System.Threading.Tasks
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module TaskResult =
    let collapse x = x |> Task.map Result.collapse

    let inline catch ([<InlineIfLambda>] f) (t: unit -> #Task) =
        task {
            try
                let! result = t ()
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

    let inline sequentialM (taskResults: #seq<unit -> TaskResult<'ok, 'err>>) : TaskResult<'ok list, 'err> =
        task {
            let mutable error = None
            let mutable i = 0

            let count = taskResults |> Seq.length
            let results = count |> Array.zeroCreate

            while error |> Option.isNone && i < count do
                let! result = taskResults |> Seq.item i |> (fun t -> t ())

                result
                |> Result.tee (fun ok -> results[i] <- ok)
                |> Result.teeError (fun e -> error <- Some e)
                |> ignore

                i <- i + 1

            return
                error
                |> Option.map Error
                |> Option.defaultWith (fun () -> results |> List.ofArray |> Ok)
        }

    let inline sequentialA (taskResults: #seq<unit -> TaskResult<'ok, 'err>>) : TaskResult<'ok list, 'err list> =
        task {
            let mutable i = 0

            let count = taskResults |> Seq.length
            let results = count |> Array.zeroCreate
            let errors = count |> Array.zeroCreate

            while i < count do
                let! result = taskResults |> Seq.item i |> (fun t -> t ())

                result
                |> Result.tee (fun ok -> results[i] <- ok)
                |> Result.teeError (fun e -> errors[i] <- e)
                |> ignore

                i <- i + 1

            return
                errors
                |> List.ofArray
                |> function
                    | [] -> results |> List.ofArray |> Ok
                    | errors -> errors |> Error
        }
