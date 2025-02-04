namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open eShop.Prelude
open FsToolkit.ErrorHandling

type Event<'payload> =
    { Data: 'payload
      OccurredAt: DateTimeOffset }

type EventHandler<'eventPayload, 'ioError> = Event<'eventPayload> -> AsyncResult<unit, 'ioError>

type HandlerName = string

type EventHandlerRegistry<'eventPayload, 'ioError> = Map<HandlerName, EventHandler<'eventPayload, 'ioError>>

type InvokedHandlers = HandlerName Set

type ReadUnprocessedEvents<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
    unit -> AsyncResult<Map<'eventId * Event<'eventPayload>, InvokedHandlers>, 'ioError>

type MarkEventAsProcessed<'eventId, 'ioError> = 'eventId -> AsyncResult<unit, 'ioError>

[<RequireQualifiedAccess>]
module EventsProcessor =
    type private Delay = TimeSpan
    type private Attempt = int

    type EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError> =
        private
            { EventHandlerRegistry: EventHandlerRegistry<'eventPayload, 'eventHandlerIoError>
              Retries: Delay list }

    let init<'state, 'eventPayload, 'eventHandlerIoError>
        : EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError> =
        { EventHandlerRegistry = Map.empty
          Retries =
            [ TimeSpan.FromMinutes(1: float)
              TimeSpan.FromMinutes(2: float)
              TimeSpan.FromMinutes(3: float)
              TimeSpan.FromMinutes(10: float) ] }

    let withRetries retries options = { options with Retries = retries }

    let registerHandler<'state, 'eventPayload, 'eventHandlerIoError>
        handlerName
        (handler: EventHandler<'eventPayload, 'eventHandlerIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>)
        =
        { options with
            EventHandlerRegistry = options.EventHandlerRegistry |> Map.add handlerName handler }

    type EventsProcessorError<'eventId, 'eventPayload, 'readEventsIoError, 'markEventsAsProcessedIoError, 'eventHandlerIoError>
        =
        | ReadEventsIoError of 'readEventsIoError
        | MarkEventsAsProcessedIoError of Event<'eventPayload> * InvokedHandlers * 'markEventsAsProcessedIoError
        | FailedEventHandlerAttempt of Attempt * 'eventId * Event<'eventPayload> * HandlerName * 'eventHandlerIoError
        | MaxEventHandlerRetriesReached of Attempt * ('eventId * Event<'eventPayload>) * HandlerName Set

    type private Command<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
        | HandleEvents of Map<'eventId * Event<'eventPayload>, EventHandlerRegistry<'eventPayload, 'ioError>>
        | TriggerRetry of Attempt * ('eventId * Event<'eventPayload>) * EventHandlerRegistry<'eventPayload, 'ioError>

    type T<'state, 'eventId, 'eventPayload, 'readEventsIoError, 'markEventAsProcessedIoError, 'eventHandlerIoError
        when 'eventId: comparison and 'eventPayload: comparison>
        internal
        (
            readEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'readEventsIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventId, 'markEventAsProcessedIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>
        ) =

        let errorEvent =
            Event<
                EventsProcessorError<
                    'eventId,
                    'eventPayload,
                    'readEventsIoError,
                    'markEventAsProcessedIoError,
                    'eventHandlerIoError
                 >
             >()

        let scheduleRetry reply attempt (event, failedHandlers) =
            task {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay

                return (attempt, event, failedHandlers) |> TriggerRetry |> reply
            }

        let handleEventAndCollectFailedHandlers
            (handlers: EventHandlerRegistry<'eventPayload, 'eventHandlerIoError>)
            attempt
            (eventId, event)
            =
            async {
                let! failedHandlers =
                    handlers
                    |> Map.toList
                    |> List.traverseAsyncResultA (fun (handlerName, handler) ->
                        event
                        |> handler
                        |> AsyncResult.teeError (fun error ->
                            FailedEventHandlerAttempt(attempt, eventId, event, handlerName, error)
                            |> errorEvent.Trigger)
                        |> AsyncResult.setError (handlerName, handler))
                    |> AsyncResult.map (fun _ -> Map.empty)
                    |> AsyncResult.mapError Map.ofList
                    |> AsyncResult.collapse

                return failedHandlers |> Option.ofMap |> Option.map (fun x -> (eventId, event), x)
            }

        let worker =
            MailboxProcessor<Command<_, _, _>>.Start(fun inbox ->
                let maxAttempts = (options.Retries |> List.length) + 1
                let scheduleRetry = scheduleRetry inbox.Post

                let rec readInbox () =
                    async {
                        let! cmd = inbox.Receive()

                        match cmd with
                        | HandleEvents(eventsAndHandlers) ->
                            let attempt = 1

                            let! toRetry =
                                eventsAndHandlers
                                |> Seq.map (fun (KeyValue(ev, handlers)) ->
                                    ev |> handleEventAndCollectFailedHandlers handlers attempt)
                                |> Async.Sequential
                                |> Async.map (Array.toList >> List.choose id)

                            toRetry |> List.map (scheduleRetry attempt) |> ignore

                        | TriggerRetry(attempt, event, handlersToRetry) ->
                            let attempt = attempt + 1

                            let! toRetryOption = event |> handleEventAndCollectFailedHandlers handlersToRetry attempt

                            toRetryOption
                            |> Option.map (fun (event, failedHandlers) ->
                                match attempt = maxAttempts with
                                | true ->
                                    let failedHandlerNames = failedHandlers |> Map.keys |> Set.ofSeq

                                    MaxEventHandlerRetriesReached(attempt, event, failedHandlerNames)
                                    |> errorEvent.Trigger

                                    Task.FromResult()

                                | false -> (event, failedHandlers) |> scheduleRetry attempt)
                            |> Option.defaultWith (fun _ -> Task.FromResult())
                            |> ignore

                        return! readInbox ()
                    }

                async {
                    let! unprocessedEventsAndHandlers =
                        readEvents ()
                        |> AsyncResult.teeError (ReadEventsIoError >> errorEvent.Trigger)
                        |> AsyncResult.defaultValue Map.empty
                        |> Async.map (
                            Map.map (fun _ invokedHandlers ->
                                options.EventHandlerRegistry
                                |> Map.filter (fun handlerName _ ->
                                    invokedHandlers |> Set.contains handlerName |> not))
                        )

                    inbox.Post(unprocessedEventsAndHandlers |> HandleEvents)

                    do! readInbox ()
                }

            )

        member this.OnError = errorEvent.Publish

        member this.HandleEvents(events) =
            events
            |> List.map (fun ev -> ev, options.EventHandlerRegistry)
            |> Map.ofList
            |> HandleEvents
            |> worker.Post

type EventsProcessor<'state, 'eventId, 'eventPayload, 'readEventsIoError, 'markEventsAsProcessedIoError, 'eventHandlerIoError
    when 'eventId: comparison and 'eventPayload: comparison> =
    EventsProcessor.T<
        'state,
        'eventId,
        'eventPayload,
        'readEventsIoError,
        'markEventsAsProcessedIoError,
        'eventHandlerIoError
     >
