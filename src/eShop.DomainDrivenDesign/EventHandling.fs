namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open eShop.Prelude
open FsToolkit.ErrorHandling

type Event<'payload> =
    { Data: 'payload
      OccurredAt: DateTimeOffset }

type EventHandler<'eventPayload, 'ioError> = Event<'eventPayload> -> AsyncResult<unit, 'ioError>

type HandlerName = string

type EventHandlerRegistry<'eventPayload, 'ioError> = Map<HandlerName, EventHandler<'eventPayload, 'ioError>>


[<RequireQualifiedAccess>]
module EventsProcessor =
    type private Delay = TimeSpan
    type private Attempt = int

    type EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        private
            { EventHandlerRegistry: EventHandlerRegistry<'eventPayload, 'ioError>
              Retries: Delay list }

    let init<'state, 'eventPayload, 'ioError> : EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        { EventHandlerRegistry = Map.empty
          Retries =
            [ TimeSpan.FromMinutes(1: float)
              TimeSpan.FromMinutes(2: float)
              TimeSpan.FromMinutes(3: float)
              TimeSpan.FromMinutes(10: float) ] }

    let withRetries retries options = { options with Retries = retries }

    let registerHandler<'state, 'eventPayload, 'ioError>
        handlerName
        (handler: EventHandler<'eventPayload, 'ioError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>)
        =
        { options with
            EventHandlerRegistry = options.EventHandlerRegistry |> Map.add handlerName handler }

    type private Command<'eventPayload, 'ioError> =
        | HandleEvents of Event<'eventPayload> list
        | TriggerRetry of Attempt * (Event<'eventPayload> * EventHandlerRegistry<'eventPayload, 'ioError>)

    type T<'state, 'eventPayload, 'ioError>
        internal (logger: ILogger, options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>) =

        let scheduleRetry reply attempt eventAndFailedHandlers =
            task {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay

                return (attempt, eventAndFailedHandlers) |> reply
            }

        let handleEventAndCollectFailedHandlers
            (handlers: EventHandlerRegistry<'eventPayload, 'ioError>)
            attempt
            event
            =
            async {
                let! failedHandlers =
                    handlers
                    |> Map.toList
                    |> List.traverseAsyncResultA (fun (handlerName, handler) ->
                        event
                        |> handler
                        |> AsyncResult.teeError (fun error ->
                            logger.LogError(
                                "#{Attempt} attempt failed to handle an event with {HandlerName}. Error: {Error}. Event: {Event}",
                                attempt,
                                handlerName,
                                error,
                                event
                            ))
                        |> AsyncResult.setError (handlerName, handler))
                    |> AsyncResult.map (fun _ -> Map.empty)
                    |> AsyncResult.mapError Map.ofList
                    |> AsyncResult.collapse

                return failedHandlers |> Option.ofMap |> Option.map (fun x -> event, x)
            }

        let worker =
            MailboxProcessor<Command<_, _>>.Start(fun inbox ->
                let maxAttempts = (options.Retries |> List.length) + 1
                let scheduleRetry = scheduleRetry (TriggerRetry >> inbox.Post)

                let rec loop () =
                    async {
                        let! cmd = inbox.Receive()

                        match cmd with
                        | HandleEvents events ->
                            let attempt = 1

                            let! toRetry =
                                events
                                |> Seq.map (handleEventAndCollectFailedHandlers options.EventHandlerRegistry attempt)
                                |> Async.Sequential
                                |> Async.map (Array.toList >> List.choose id)

                            toRetry |> List.map (scheduleRetry attempt) |> ignore

                        | TriggerRetry(attempt, (event, handlersToRetry)) ->
                            let attempt = attempt + 1

                            let! toRetryOption = event |> handleEventAndCollectFailedHandlers handlersToRetry attempt

                            toRetryOption
                            |> Option.map (fun (event, failedHandlers) ->
                                match attempt = maxAttempts with
                                | true ->
                                    logger.LogError(
                                        "Failed to handle event on max attempt #{MaxAttempts}. Event: {Event}. Could not successfully invoke following handlers: {HandlerNames}",
                                        maxAttempts,
                                        event,
                                        (failedHandlers |> Map.keys |> String.concat ",")
                                    )

                                    Task.FromResult()

                                | false -> (event, failedHandlers) |> scheduleRetry attempt)
                            |> Option.defaultWith (fun _ -> Task.FromResult())
                            |> ignore

                        return! loop ()
                    }

                loop ())

        member this.HandleEvents(events) = worker.Post(events |> HandleEvents)

type EventsProcessor<'state, 'eventPayload, 'ioError> = EventsProcessor.T<'state, 'eventPayload, 'ioError>
