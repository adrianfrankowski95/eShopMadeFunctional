namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open eShop.Prelude
open FsToolkit.ErrorHandling

type Event<'payload> =
    { Data: 'payload
      OccurredAt: DateTimeOffset }

type EventHandler<'eventPayload, 'ioError> = Event<'eventPayload> -> AsyncResult<unit, 'ioError>

type EventHandlerName = string

type SuccessfulEventHandlers = EventHandlerName Set

type EventHandlerRegistry<'eventPayload, 'ioError> = Map<EventHandlerName, EventHandler<'eventPayload, 'ioError>>

type AggregateType = string

type ReadUnprocessedEvents<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
    AggregateType -> AsyncResult<Map<'eventId * Event<'eventPayload>, SuccessfulEventHandlers>, 'ioError>

type PersistSuccessfulEventHandlers<'eventId, 'ioError> =
    'eventId -> SuccessfulEventHandlers -> AsyncResult<unit, 'ioError>

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

    type EventsProcessorError<'eventId, 'eventPayload, 'readUnprocessedEventsIoError, 'persistEventHandlersIoError, 'markEventsAsProcessedIoError, 'eventHandlerIoError>
        =
        | ReadingUnprocessedEventsFailed of 'readUnprocessedEventsIoError
        | PersistingSuccessfulEventHandlersFailed of 'eventId * SuccessfulEventHandlers * 'persistEventHandlersIoError
        | MarkingEventAsProcessedFailed of 'markEventsAsProcessedIoError
        | EventHandlerFailed of Attempt * 'eventId * Event<'eventPayload> * EventHandlerName * 'eventHandlerIoError
        | MaxEventProcessingRetriesReached of Attempt * ('eventId * Event<'eventPayload>) * EventHandlerName Set

    type private Command<'eventId, 'eventPayload, 'ioError when 'eventId: comparison and 'eventPayload: comparison> =
        | Publish of Map<'eventId * Event<'eventPayload>, EventHandlerRegistry<'eventPayload, 'ioError>>
        | Retry of Attempt * ('eventId * Event<'eventPayload>) * EventHandlerRegistry<'eventPayload, 'ioError>

    type T<'state, 'eventId, 'eventPayload, 'readUnprocessedEventsIoError, 'persistEventHandlersIoError, 'markEventAsProcessedIoError, 'eventHandlerIoError
        when 'eventId: comparison and 'eventPayload: comparison>
        internal
        (
            readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'readUnprocessedEventsIoError>,
            persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'persistEventHandlersIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventId, 'markEventAsProcessedIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>
        ) =

        let errorEvent =
            Event<
                EventsProcessorError<
                    'eventId,
                    'eventPayload,
                    'readUnprocessedEventsIoError,
                    'persistEventHandlersIoError,
                    'markEventAsProcessedIoError,
                    'eventHandlerIoError
                 >
             >()

        let scheduleRetry postCommand attempt (event, failedHandlers) =
            async {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay |> Async.AwaitTask

                return (attempt, event, failedHandlers) |> Retry |> postCommand
            }

        let processEvent maxAttempts scheduleRetry attempt handlers (eventId, event) =
            async {
                let! successfulHandlers, failedHandlers =
                    handlers
                    |> Seq.map (fun (KeyValue(handlerName, handler)) ->
                        event
                        |> handler
                        |> AsyncResult.map (fun _ -> handlerName)
                        |> AsyncResult.teeError (fun error ->
                            EventHandlerFailed(attempt, eventId, event, handlerName, error)
                            |> errorEvent.Trigger)
                        |> AsyncResult.setError (handlerName, handler))
                    |> Async.Sequential
                    |> Async.map (Result.extractList >> Tuple.mapFst Set.ofList)

                do!
                    successfulHandlers
                    |> persistSuccessfulHandlers eventId
                    |> AsyncResult.teeError (fun error ->
                        PersistingSuccessfulEventHandlersFailed(eventId, successfulHandlers, error)
                        |> errorEvent.Trigger)
                    |> AsyncResult.ignoreError

                do!
                    match failedHandlers with
                    | [] ->
                        eventId
                        |> markEventAsProcessed
                        |> AsyncResult.teeError (MarkingEventAsProcessedFailed >> errorEvent.Trigger)
                        |> AsyncResult.ignoreError
                    | failedHandlers ->
                        match attempt = maxAttempts with
                        | false -> ((eventId, event), failedHandlers |> Map.ofList) |> scheduleRetry attempt
                        | true ->
                            (attempt, (eventId, event), failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> errorEvent.Trigger

                            Async.retn ()
            }

        let handleCommand maxAttempts scheduleRetry cmd =
            let processEvent = processEvent maxAttempts scheduleRetry

            async {
                match cmd with
                | Publish(eventsWithHandlers) ->
                    let attempt = 1

                    do!
                        eventsWithHandlers
                        |> Seq.map (fun (KeyValue(event, handlers)) -> event |> processEvent attempt handlers)
                        |> Async.Sequential
                        |> Async.Ignore

                | Retry(attempt, event, handlersToRetry) ->
                    let attempt = attempt + 1

                    do! event |> processEvent attempt handlersToRetry
            }

        let restoreState () =
            readUnprocessedEvents typeof<'state>.Name
            |> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> errorEvent.Trigger)
            |> AsyncResult.defaultValue Map.empty
            |> Async.map (Map.mapValues (Map.removeKeys options.EventHandlerRegistry))

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

                    inbox.Post(state |> Publish)

                    do! loop ()
                })

        member this.OnError = errorEvent.Publish.Add

        member this.Publish(events) =
            events
            |> Seq.map (fun ev -> ev, options.EventHandlerRegistry)
            |> Map.ofSeq
            |> Publish
            |> processor.Post

    let build
        (readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'readUnprocessedEventsIoError>)
        (persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'persistEventHandlersIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventId, 'markEventAsProcessedIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>)
        =
        T(readUnprocessedEvents, persistSuccessfulHandlers, markEventAsProcessed, options)

type EventsProcessor<'state, 'eventId, 'eventPayload, 'readUnprocessedEventsIoError, 'persistEventHandlersIoError, 'markEventsAsProcessedIoError, 'eventHandlerIoError
    when 'eventId: comparison and 'eventPayload: comparison> =
    EventsProcessor.T<
        'state,
        'eventId,
        'eventPayload,
        'readUnprocessedEventsIoError,
        'persistEventHandlersIoError,
        'markEventsAsProcessedIoError,
        'eventHandlerIoError
     >
