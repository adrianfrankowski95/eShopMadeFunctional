namespace eShop.Prelude

open FsToolkit.ErrorHandling
open System

type Reply<'msg> = 'msg -> unit

type AgentMessageHandler<'st, 'msg, 'err> = 'st -> 'msg -> Reply<'msg> -> AsyncResult<'st option, 'err>

type AgentErrorHandler<'st, 'msg, 'err> = 'st -> 'msg -> 'err -> Async<'st option>

type Agent<'st, 'msg, 'err>
    internal
    (
        initState: 'st,
        messageHandler: AgentMessageHandler<'st, 'msg, 'err>,
        onError: AgentErrorHandler<'st, 'msg, 'err>,
        onDispose: 'st -> Async<unit>
    ) =

    [<TailCall>]
    let agent =
        MailboxProcessor<'msg>.Start(fun inbox ->
            let rec loop state =
                async {
                    let! msg = inbox.Receive()
                    let! result = messageHandler state msg inbox.Post

                    let! maybeNewState =
                        result
                        |> Result.map Async.singleton
                        |> Result.mapError (onError state msg)
                        |> Result.collapse

                    return!
                        maybeNewState
                        |> Option.map loop
                        |> Option.defaultWith (fun () -> onDispose state)
                }

            loop initState)

    member _.Post<'msg>(msg: 'msg) = msg |> agent.Post

    interface IDisposable with
        member _.Dispose() = agent.Dispose()

[<RequireQualifiedAccess>]
module Agent =
    type Options<'st, 'msg, 'err> =
        private
            { InitState: 'st
              MessageHandler: AgentMessageHandler<'st, 'msg, 'err>
              ErrorHandler: AgentErrorHandler<'st, 'msg, 'err>
              Dispose: 'st -> Async<unit> }

    let init state =
        { InitState = state
          MessageHandler = fun st _ _ -> st |> AsyncResultOption.singleton
          ErrorHandler = fun st _ _ -> st |> AsyncOption.some
          Dispose = fun _ -> Async.singleton () }

    let withMessageHandler messageHandler options =
        { options with
            MessageHandler = messageHandler }

    let withErrorHandler errorHandler options =
        { options with
            ErrorHandler = errorHandler }

    let withDisposal dispose options = { options with Dispose = dispose }

    let start options =
        new Agent<'st, 'msg, 'err>(options.InitState, options.MessageHandler, options.ErrorHandler, options.Dispose)
