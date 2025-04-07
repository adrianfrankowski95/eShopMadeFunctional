namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open eShop.Prelude
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

[<Measure>]
type eventId

type EventId = Id<eventId>

type Event<'payload> =
    { Id: EventId
      Data: 'payload
      OccurredAt: DateTimeOffset }

module Event =
    let mapPayload (newData: 'b) (ev: Event<'a>) : Event<'b> =
        { Id = ev.Id
          Data = newData
          OccurredAt = ev.OccurredAt }

    let typeName<'payload> =
        let payloadType = typeof<'payload>

        payloadType.DeclaringType.Name + payloadType.Name


type EventHandler<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> -> AsyncResult<unit, 'ioError>

type EventHandlerName = string

type SuccessfulEventHandlers = EventHandlerName Set

type EventHandlerRegistry<'state, 'eventPayload, 'ioError> =
    Map<EventHandlerName, EventHandler<'state, 'eventPayload, 'ioError>>

type PersistEvents<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> list -> AsyncResult<unit, 'ioError>

type ReadUnprocessedEvents<'state, 'eventPayload, 'ioError> =
    unit -> AsyncResult<(AggregateId<'state> * Event<'eventPayload> * SuccessfulEventHandlers) list, 'ioError>

type PersistSuccessfulEventHandlers<'ioError> = EventId -> SuccessfulEventHandlers -> AsyncResult<unit, 'ioError>

type MarkEventAsProcessed<'ioError> = EventId -> AsyncResult<unit, 'ioError>

[<RequireQualifiedAccess>]
module EventsProcessor =
    type private Delay = TimeSpan
    type private Attempt = int

    type EventsProcessorError<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
        | ReadingUnprocessedEventsFailed of 'eventLogIoError
        | PersistingSuccessfulEventHandlersFailed of
            AggregateId<'state> *
            Event<'eventPayload> *
            SuccessfulEventHandlers *
            'eventLogIoError
        | MarkingEventAsProcessedFailed of Event<'eventPayload> * 'eventLogIoError
        | EventHandlerFailed of
            Attempt *
            AggregateId<'state> *
            Event<'eventPayload> *
            EventHandlerName *
            'eventHandlingIoError
        | MaxEventProcessingRetriesReached of
            Attempt *
            AggregateId<'state> *
            Event<'eventPayload> *
            EventHandlerName Set

    type EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
        private
            { EventHandlerRegistry: EventHandlerRegistry<'state, 'eventPayload, 'eventHandlingIoError>
              Retries: Delay list
              ErrorHandler:
                  (EventsProcessorError<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> -> unit) option }

    let init<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        : EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
        { EventHandlerRegistry = Map.empty
          Retries = ([ 1; 2; 3; 10 ]: float list) |> List.map TimeSpan.FromMinutes
          ErrorHandler = None }

    let withErrorHandler handler options =
        { options with
            ErrorHandler = handler |> Some }

    let withRetries retries options = { options with Retries = retries }

    let registerHandler<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        handlerName
        (handler: EventHandler<'state, 'eventPayload, 'eventHandlingIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>)
        =
        { options with
            EventHandlerRegistry = options.EventHandlerRegistry |> Map.add handlerName handler }

    type private Command<'state, 'eventPayload, 'ioError> =
        | Process of
            (AggregateId<'state> * Event<'eventPayload> * EventHandlerRegistry<'state, 'eventPayload, 'ioError>) list
        | Retry of
            Attempt *
            AggregateId<'state> *
            Event<'eventPayload> *
            EventHandlerRegistry<'state, 'eventPayload, 'ioError>

    type T<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        internal
        (
            persistEvents: PersistEvents<'state, 'eventPayload, 'eventLogIoError>,
            readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventPayload, 'eventLogIoError>,
            persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventLogIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventLogIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        ) =
        let handleError err =
            options.ErrorHandler |> Option.teeSome (fun handler -> handler err) |> ignore

        let scheduleRetry postCommand attempt aggregateId event failedHandlers =
            async {
                do! options.Retries |> List.item (attempt - 1) |> Task.Delay |> Async.AwaitTask

                return (attempt, aggregateId, event, failedHandlers) |> Retry |> postCommand
            }

        let maxAttempts = (options.Retries |> List.length) + 1

        let processEvent scheduleRetry aggregateId event attempt handlers =
            async {
                let! successfulHandlers, failedHandlers =
                    handlers
                    |> Seq.map (fun (KeyValue(handlerName, handler)) ->
                        event
                        |> handler aggregateId
                        |> AsyncResult.map (fun _ -> handlerName)
                        |> AsyncResult.teeError (fun error ->
                            EventHandlerFailed(attempt, aggregateId, event, handlerName, error)
                            |> handleError)
                        |> AsyncResult.setError (handlerName, handler))
                    |> Async.Sequential
                    |> Async.map (Result.extractList >> Tuple.mapFst Set.ofList)

                do!
                    successfulHandlers
                    |> persistSuccessfulHandlers event.Id
                    |> AsyncResult.teeError (fun error ->
                        PersistingSuccessfulEventHandlersFailed(aggregateId, event, successfulHandlers, error)
                        |> handleError)
                    |> AsyncResult.ignoreError

                do!
                    match failedHandlers with
                    | [] ->
                        event.Id
                        |> markEventAsProcessed
                        |> AsyncResult.teeError (fun err ->
                            (event, err) |> MarkingEventAsProcessedFailed |> handleError)
                        |> AsyncResult.ignoreError
                    | failedHandlers ->
                        match attempt = maxAttempts with
                        | false -> failedHandlers |> Map.ofList |> scheduleRetry attempt aggregateId event
                        | true ->
                            (attempt, aggregateId, event, failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> handleError
                            |> Async.retn
            }

        let handleCommand scheduleRetry cmd =
            let processEvent = processEvent scheduleRetry

            async {
                match cmd with
                | Process(processingData) ->
                    let attempt = 1

                    do!
                        processingData
                        |> List.map (fun (aggregateId, event, handlers) ->
                            handlers |> processEvent aggregateId event attempt)
                        |> Async.Sequential
                        |> Async.Ignore

                | Retry(attempt, aggregateId, event, handlersToRetry) ->
                    let attempt = attempt + 1

                    do! handlersToRetry |> processEvent aggregateId event attempt
            }

        let restoreState =
            readUnprocessedEvents
            >> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> handleError)
            >> AsyncResult.defaultValue []
            >> Async.map (
                List.map (fun (aggregateId, event, successfulHandlers) ->
                    aggregateId, event, options.EventHandlerRegistry |> Map.removeKeys successfulHandlers)
            )

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

                    inbox.Post(state |> Process)

                    do! loop ()
                })

        member this.Process =
            fun aggregateId events ->
                asyncResult {
                    do! events |> persistEvents aggregateId

                    events
                    |> List.sortBy _.OccurredAt
                    |> List.map (fun event -> aggregateId, event, options.EventHandlerRegistry)
                    |> Process
                    |> processor.Post
                }

        interface IDisposable with
            member this.Dispose() = processor.Dispose()

    let build
        (persistEvents: PersistEvents<'state, 'eventPayload, 'eventLogIoError>)
        (readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventPayload, 'eventLogIoError>)
        (persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventLogIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventLogIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>)
        =
        new T<_, _, _, _>(
            persistEvents,
            readUnprocessedEvents,
            persistSuccessfulHandlers,
            markEventAsProcessed,
            options
        )

type EventsProcessor<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
    EventsProcessor.T<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
