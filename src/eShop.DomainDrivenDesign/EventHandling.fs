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

type ReadUnprocessedEvents<'eventId, 'eventPayload, 'ioError when 'eventId: comparison> =
    AggregateType -> AsyncResult<Map<'eventId, Event<'eventPayload> * SuccessfulEventHandlers>, 'ioError>

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

    type EventsProcessorError<'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlerIoError> =
        | ReadingUnprocessedEventsFailed of 'eventLogIoError
        | PersistingSuccessfulEventHandlersFailed of 'eventId * SuccessfulEventHandlers * 'eventLogIoError
        | MarkingEventAsProcessedFailed of 'eventLogIoError
        | EventHandlerFailed of Attempt * 'eventId * Event<'eventPayload> * EventHandlerName * 'eventHandlerIoError
        | MaxEventProcessingRetriesReached of Attempt * 'eventId * Event<'eventPayload> * EventHandlerName Set

    type private Command<'eventId, 'eventPayload, 'ioError when 'eventId: comparison> =
        | Publish of Map<'eventId, Event<'eventPayload> * EventHandlerRegistry<'eventPayload, 'ioError>>
        | Retry of Attempt * 'eventId * Event<'eventPayload> * EventHandlerRegistry<'eventPayload, 'ioError>

    type T<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlerIoError when 'eventId: comparison>
        internal
        (
            readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'eventLogIoError>,
            persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'eventLogIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventId, 'eventLogIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>
        ) =
        let errorEvent =
            Event<EventsProcessorError<'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlerIoError>>()

        let scheduleRetry postCommand attempt eventId eventData failedHandlers =
            async {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay |> Async.AwaitTask

                return (attempt, eventId, eventData, failedHandlers) |> Retry |> postCommand
            }

        let maxAttempts = (options.Retries |> List.length) + 1

        let processEvent scheduleRetry eventId eventData attempt handlers =
            async {
                let! successfulHandlers, failedHandlers =
                    handlers
                    |> Seq.map (fun (KeyValue(handlerName, handler)) ->
                        eventData
                        |> handler
                        |> AsyncResult.map (fun _ -> handlerName)
                        |> AsyncResult.teeError (fun error ->
                            EventHandlerFailed(attempt, eventId, eventData, handlerName, error)
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
                        | false -> failedHandlers |> Map.ofList |> scheduleRetry attempt eventId eventData
                        | true ->
                            (attempt, eventId, eventData, failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> errorEvent.Trigger
                            |> Async.retn
            }

        let handleCommand scheduleRetry cmd =
            let processEvent = processEvent scheduleRetry

            async {
                match cmd with
                | Publish(eventsWithHandlers) ->
                    let attempt = 1

                    do!
                        eventsWithHandlers
                        |> Seq.map (fun (KeyValue(eventId, (eventData, handlers))) ->
                            handlers |> processEvent eventId eventData attempt)
                        |> Async.Sequential
                        |> Async.Ignore

                | Retry(attempt, eventId, eventData, handlersToRetry) ->
                    let attempt = attempt + 1

                    do! handlersToRetry |> processEvent eventId eventData attempt
            }

        let restoreState () =
            readUnprocessedEvents typeof<'state>.Name
            |> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> errorEvent.Trigger)
            |> AsyncResult.defaultValue Map.empty
            |> Async.map (Map.mapValues (Tuple.mapSnd (Map.removeKeys options.EventHandlerRegistry)))

        let processor =
            MailboxProcessor<Command<_, _, _>>.Start(fun inbox ->
                let scheduleRetry = scheduleRetry inbox.Post
                let handleCommand = handleCommand scheduleRetry

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
            |> Seq.map (fun (eventId, eventData) -> (eventId, (eventData, options.EventHandlerRegistry)))
            |> Map.ofSeq
            |> Publish
            |> processor.Post

    let build
        (readUnprocessedEvents: ReadUnprocessedEvents<'eventId, 'eventPayload, 'eventLogIoError>)
        (persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'eventLogIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventId, 'eventLogIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>)
        =
        T(readUnprocessedEvents, persistSuccessfulHandlers, markEventAsProcessed, options)

type EventsProcessor<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlerIoError when 'eventId: comparison>
    = EventsProcessor.T<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlerIoError>
