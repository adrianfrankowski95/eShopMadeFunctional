namespace eShop.Prelude

open System.Threading.Tasks
open FsToolkit.ErrorHandling
open System

type Work<'st, 'err> = 'st -> TaskResult<'st option, 'err>

type WorkerErrorHandler<'st, 'err> = 'st -> 'err -> Task<'st option>

type Worker<'st, 'err>
    internal (initState: 'st, interval: TimeSpan, work: Work<'st, 'err>, onError: WorkerErrorHandler<'st, 'err>) =
    
    [<TailCall>]
    let rec loop (state: 'st) : Task<unit> =
        backgroundTask {
            let! result = work state

            let! maybeNewState =
                result
                |> Result.map Task.singleton
                |> Result.mapError (onError state)
                |> Result.collapse

            return!
                maybeNewState
                |> Option.map (fun newState ->
                    task {
                        do! Task.Delay interval
                        return! loop newState
                    })
                |> Option.defaultWith Task.singleton
        }

    let loopTask = loop initState

    interface IDisposable with
        member _.Dispose() = loopTask.Dispose()


[<RequireQualifiedAccess>]
module Worker =
    type Options<'st, 'err> =
        private
            { InitState: 'st
              Interval: TimeSpan
              Work: Work<'st, 'err>
              ErrorHandler: WorkerErrorHandler<'st, 'err> }

    let init initState =
        { InitState = initState
          Interval = TimeSpan.FromSeconds 5.0
          Work = fun st -> st |> TaskResultOption.singleton
          ErrorHandler = fun st _ -> st |> Some |> Task.FromResult }

    let withWork work options = { options with Work = work }

    let withInternal interval options = { options with Interval = interval }

    let withErrorHandler errorHandler options =
        { options with
            ErrorHandler = errorHandler }

    let start options =
        new Worker<'st, 'err>(options.InitState, options.Interval, options.Work, options.ErrorHandler)
