namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open eShop.Prelude
open FsToolkit.ErrorHandling

type Event<'payload> =
    { Data: 'payload
      OccurredAt: DateTimeOffset }

type EventHandler<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> -> AsyncResult<unit, 'ioError>

type EventHandlerName = string

type SuccessfulEventHandlers = EventHandlerName Set

type EventHandlerRegistry<'state, 'eventPayload, 'ioError> =
    Map<EventHandlerName, EventHandler<'state, 'eventPayload, 'ioError>>

type AggregateType = string

type ReadUnprocessedEvents<'state, 'eventId, 'eventPayload, 'ioError> =
    unit
        -> AsyncResult<(AggregateId<'state> * 'eventId * Event<'eventPayload> * SuccessfulEventHandlers) list, 'ioError>

type PersistSuccessfulEventHandlers<'eventId, 'ioError> =
    'eventId -> SuccessfulEventHandlers -> AsyncResult<unit, 'ioError>

type MarkEventAsProcessed<'eventId, 'ioError> = 'eventId -> AsyncResult<unit, 'ioError>

[<RequireQualifiedAccess>]
module EventsProcessor =
    type private Delay = TimeSpan
    type private Attempt = int

    type EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        private
            { EventHandlerRegistry: EventHandlerRegistry<'state, 'eventPayload, 'ioError>
              Retries: Delay list }

    let init<'state, 'eventPayload, 'ioError> : EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        { EventHandlerRegistry = Map.empty
          Retries = ([ 1; 2; 3; 10 ]: float list) |> List.map TimeSpan.FromMinutes }

    let withRetries retries options = { options with Retries = retries }

    let registerHandler<'state, 'eventPayload, 'ioError>
        handlerName
        (handler: EventHandler<'state, 'eventPayload, 'ioError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>)
        =
        { options with
            EventHandlerRegistry = options.EventHandlerRegistry |> Map.add handlerName handler }

    type EventsProcessorError<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
        | ReadingUnprocessedEventsFailed of 'eventLogIoError
        | PersistingSuccessfulEventHandlersFailed of
            AggregateId<'state> *
            'eventId *
            SuccessfulEventHandlers *
            'eventLogIoError
        | MarkingEventAsProcessedFailed of 'eventLogIoError
        | EventHandlerFailed of
            Attempt *
            AggregateId<'state> *
            'eventId *
            Event<'eventPayload> *
            EventHandlerName *
            'eventHandlingIoError
        | MaxEventProcessingRetriesReached of
            Attempt *
            AggregateId<'state> *
            'eventId *
            Event<'eventPayload> *
            EventHandlerName Set

    type private Command<'state, 'eventId, 'eventPayload, 'ioError> =
        | Publish of
            (AggregateId<'state> *
            'eventId *
            Event<'eventPayload> *
            EventHandlerRegistry<'state, 'eventPayload, 'ioError>) list
        | Retry of
            Attempt *
            AggregateId<'state> *
            'eventId *
            Event<'eventPayload> *
            EventHandlerRegistry<'state, 'eventPayload, 'ioError>

    type T<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        internal
        (
            readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventId, 'eventPayload, 'eventLogIoError>,
            persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'eventLogIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventId, 'eventLogIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlingIoError>
        ) =
        let errorEvent =
            Event<EventsProcessorError<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>>()

        let scheduleRetry postCommand attempt aggregateId eventId eventData failedHandlers =
            async {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay |> Async.AwaitTask

                return
                    (attempt, aggregateId, eventId, eventData, failedHandlers)
                    |> Retry
                    |> postCommand
            }

        let maxAttempts = (options.Retries |> List.length) + 1

        let processEvent scheduleRetry aggregateId eventId eventData attempt handlers =
            async {
                let! successfulHandlers, failedHandlers =
                    handlers
                    |> Seq.map (fun (KeyValue(handlerName, handler)) ->
                        eventData
                        |> handler aggregateId
                        |> AsyncResult.map (fun _ -> handlerName)
                        |> AsyncResult.teeError (fun error ->
                            EventHandlerFailed(attempt, aggregateId, eventId, eventData, handlerName, error)
                            |> errorEvent.Trigger)
                        |> AsyncResult.setError (handlerName, handler))
                    |> Async.Sequential
                    |> Async.map (Result.extractList >> Tuple.mapFst Set.ofList)

                do!
                    successfulHandlers
                    |> persistSuccessfulHandlers eventId
                    |> AsyncResult.teeError (fun error ->
                        PersistingSuccessfulEventHandlersFailed(aggregateId, eventId, successfulHandlers, error)
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
                        | false ->
                            failedHandlers
                            |> Map.ofList
                            |> scheduleRetry attempt aggregateId eventId eventData
                        | true ->
                            (attempt, aggregateId, eventId, eventData, failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> errorEvent.Trigger
                            |> Async.retn
            }

        let handleCommand scheduleRetry cmd =
            let processEvent = processEvent scheduleRetry

            async {
                match cmd with
                | Publish(publishData) ->
                    let attempt = 1

                    do!
                        publishData
                        |> List.map (fun (aggregateId, eventId, eventData, handlers) ->
                            handlers |> processEvent aggregateId eventId eventData attempt)
                        |> Async.Sequential
                        |> Async.Ignore

                | Retry(attempt, aggregateId, eventId, eventData, handlersToRetry) ->
                    let attempt = attempt + 1

                    do! handlersToRetry |> processEvent aggregateId eventId eventData attempt
            }

        let restoreState =
            readUnprocessedEvents
            >> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> errorEvent.Trigger)
            >> AsyncResult.defaultValue []
            >> Async.map (
                List.map (fun (aggregateId, eventId, event, successfulHandlers) ->
                    aggregateId, eventId, event, options.EventHandlerRegistry |> Map.removeKeys successfulHandlers)
            )

        let processor =
            MailboxProcessor<Command<_, _, _, _>>.Start(fun inbox ->
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

        member this.Publish(aggregateId, events) =
            events
            |> List.sortBy (snd >> _.OccurredAt)
            |> List.map (fun (eventId, eventData) -> aggregateId, eventId, eventData, options.EventHandlerRegistry)
            |> Publish
            |> processor.Post

    let build
        (readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventId, 'eventPayload, 'eventLogIoError>)
        (persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventId, 'eventLogIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventId, 'eventLogIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventHandlerIoError>)
        =
        T(readUnprocessedEvents, persistSuccessfulHandlers, markEventAsProcessed, options)

type EventsProcessor<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError when 'eventId: comparison>
    = EventsProcessor.T<'state, 'eventId, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
