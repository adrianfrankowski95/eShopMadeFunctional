namespace eShop.Prelude

open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open System

type AgentMessageHandler<'st, 'msg, 'err> = 'st -> 'msg -> TaskResult<'st option, 'err>

type AgentErrorHandler<'st, 'msg, 'err> = 'st -> 'msg -> 'err -> Task<'st option>

type Agent<'st, 'msg, 'err>
    internal
    (
        initState: 'st,
        messageHandler: AgentMessageHandler<'st, 'msg, 'err>,
        onError: AgentErrorHandler<'st, 'msg, 'err>,
        onDispose: 'st -> Task<unit>
    ) =
    let channel =
        Channel.CreateUnbounded<'msg>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    let cts = new CancellationTokenSource()

    [<TailCall>]
    let rec loop (state: 'st) : Task<unit> =
        backgroundTask {
            try
                let! msg = channel.Reader.ReadAsync(cts.Token)
                let! result = msg |> messageHandler state

                let! maybeNewState =
                    result
                    |> Result.map Task.singleton
                    |> Result.mapError (onError state msg)
                    |> Result.collapse

                return!
                    maybeNewState
                    |> Option.map loop
                    |> Option.defaultWith (cts.Cancel >> Task.singleton)

            with _ ->
                do! onDispose state
                return ()
        }

    let loopTask = loop initState

    member inline _.Post<'msg>(msg: 'msg) = channel.Writer.WriteAsync(msg)

    interface IDisposable with
        member _.Dispose() =
            channel.Writer.Complete()

            if not cts.IsCancellationRequested then
                cts.Cancel()
                cts.Dispose()

            loopTask.Dispose()

[<RequireQualifiedAccess>]
module Agent =
    type Options<'st, 'msg, 'err> =
        private
            { InitState: 'st
              MessageHandler: AgentMessageHandler<'st, 'msg, 'err>
              ErrorHandler: AgentErrorHandler<'st, 'msg, 'err>
              Dispose: 'st -> Task<unit> }

    let init initState =
        { InitState = initState
          MessageHandler = fun st _ -> st |> TaskResultOption.singleton
          ErrorHandler = fun st _ _ -> st |> Some |> Task.FromResult
          Dispose = fun _ -> Task.singleton () }

    let withMessageHandler messageHandler options =
        { options with
            MessageHandler = messageHandler }

    let withErrorHandler errorHandler options =
        { options with
            ErrorHandler = errorHandler }

    let withDisposal dispose options = { options with Dispose = dispose }

    let start options =
        new Agent<'st, 'msg, 'err>(options.InitState, options.MessageHandler, options.ErrorHandler, options.Dispose)
