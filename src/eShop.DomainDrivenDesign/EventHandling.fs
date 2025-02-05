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

type InvokedHandlers = HandlerName Set

type EventHandlerRegistry<'eventPayload, 'ioError> = Map<HandlerName, EventHandler<'eventPayload, 'ioError>>

type ReadUnprocessedEvents<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
    unit -> AsyncResult<Map<'eventId * Event<'eventPayload>, InvokedHandlers>, 'ioError>

type MarkEventAsProcessed<'eventId, 'ioError> = 'eventId -> AsyncResult<unit, 'ioError>

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
          Retries = ([ 1; 2; 3; 10 ]: float list) |> List.map TimeSpan.FromMinutes }

    let withRetries retries options = { options with Retries = retries }

    let registerHandler<'state, 'eventPayload, 'ioError>
        handlerName
        (handler: EventHandler<'eventPayload, 'ioError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>)
        =
        { options with
            EventHandlerRegistry = options.EventHandlerRegistry |> Map.add handlerName handler }

    type EventsProcessorError<'eventId, 'eventPayload, 'readEventsIoError, 'markEventsAsProcessedIoError, 'eventHandlerIoError>
        =
        | ReadingUnprocessedEventsFailed of 'readEventsIoError
        | MarkingEventAsProcessedFailed of 'eventId * InvokedHandlers * 'markEventsAsProcessedIoError
        | EventHandlerFailed of Attempt * 'eventId * Event<'eventPayload> * HandlerName * 'eventHandlerIoError
        | MaxEventProcessingRetriesReached of Attempt * ('eventId * Event<'eventPayload>) * HandlerName Set

    type private Command<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
        | Process of Map<'eventId * Event<'eventPayload>, EventHandlerRegistry<'eventPayload, 'ioError>>
        | Retry of Attempt * ('eventId * Event<'eventPayload>) * EventHandlerRegistry<'eventPayload, 'ioError>

    type T<'state, 'eventId, 'eventPayload, 'readEventsIoError, 'markEventAsProcessedIoError, 'eventHandlerIoError
        when 'eventId: comparison and 'eventPayload: comparison>
        internal
        (
            readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'readEventsIoError>,
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
            async {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay |> Async.AwaitTask

                return (attempt, event, failedHandlers) |> Retry |> reply
            }

        let processEvent attempt handlers (eventId, event) =
            handlers
            |> Seq.traverseAsyncResultA (fun (KeyValue(handlerName, handler)) ->
                event
                |> handler
                |> AsyncResult.teeError (fun error ->
                    EventHandlerFailed(attempt, eventId, event, handlerName, error)
                    |> errorEvent.Trigger)
                |> AsyncResult.setError (handlerName, handler))
            |> AsyncResult.map (fun _ -> eventId, handlers |> Map.keys |> Set.ofSeq)
            |> AsyncResult.mapError (fun failedHandlers -> attempt, (eventId, event), failedHandlers |> Map.ofSeq)

        let handleProcessingResult maxAttempts scheduleRetry result =
            match result with
            | Ok(eventId, invokedHandlers) ->
                eventId
                |> markEventAsProcessed
                |> AsyncResult.teeError (fun ioError ->
                    (eventId, invokedHandlers, ioError)
                    |> MarkingEventAsProcessedFailed
                    |> errorEvent.Trigger)
                |> AsyncResult.defaultValue ()

            | Error(attempt, event, failedHandlers) ->
                match attempt = maxAttempts with
                | true ->
                    (attempt, event, failedHandlers |> Map.keys |> Set.ofSeq)
                    |> MaxEventProcessingRetriesReached
                    |> errorEvent.Trigger

                    Async.retn ()
                | false -> (event, failedHandlers) |> scheduleRetry attempt

        let handleCommand maxAttempts scheduleRetry cmd =
            let handleProcessingResult = handleProcessingResult maxAttempts scheduleRetry

            async {
                match cmd with
                | Process(eventsWithHandlers) ->
                    let attempt = 1

                    let! results =
                        eventsWithHandlers
                        |> Seq.map (fun (KeyValue(event, handlers)) -> event |> processEvent attempt handlers)
                        |> Async.Sequential

                    do! results |> Array.map handleProcessingResult |> Async.Sequential |> Async.Ignore

                | Retry(attempt, event, handlersToRetry) ->
                    let attempt = attempt + 1

                    let! result = event |> processEvent attempt handlersToRetry

                    do! result |> handleProcessingResult
            }

        let restoreState =
            readUnprocessedEvents
            >> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> errorEvent.Trigger)
            >> AsyncResult.defaultValue Map.empty
            >> Async.map (Map.mapValues (Map.removeKeys options.EventHandlerRegistry))

        let processor =
            MailboxProcessor<Command<_, _, _>>.Start(fun inbox ->
                let maxAttempts = (options.Retries |> List.length) + 1
                let scheduleRetry = scheduleRetry inbox.Post
                let handleCommand = handleCommand maxAttempts scheduleRetry

                let rec loop () =
                    async {
                        let! cmd = inbox.Receive()

                        do! cmd |> handleCommand

                        return! loop ()
                    }

                async {
                    let! state = restoreState ()

                    inbox.Post(state |> Process)

                    do! loop ()
                })

        member this.OnError = errorEvent.Publish.Add

        member this.Process(events) =
            events
            |> Seq.map (fun ev -> ev, options.EventHandlerRegistry)
            |> Map.ofSeq
            |> Process
            |> processor.Post

    let build
        (readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'readEventsIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventId, 'markEventAsProcessedIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>)
        =
        T(readUnprocessedEvents, markEventAsProcessed, options)

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
