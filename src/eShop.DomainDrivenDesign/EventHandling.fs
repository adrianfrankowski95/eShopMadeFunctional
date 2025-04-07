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

    type ProcessEvent<'state, 'eventPayload, 'ioError> =
        { Attempt: Attempt
          AggregateId: AggregateId<'state>
          Event: Event<'eventPayload>
          Handlers: EventHandlerRegistry<'state, 'eventPayload, 'ioError> }

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

        let scheduleRetry postCommand cmd =
            async {
                do! options.Retries |> List.item (cmd.Attempt - 1) |> Task.Delay |> Async.AwaitTask

                return cmd |> postCommand
            }

        let maxAttempts = (options.Retries |> List.length) + 1

        let processEvent scheduleRetry cmd =
            async {
                let! successfulHandlers, failedHandlers =
                    cmd.Handlers
                    |> Seq.map (fun (KeyValue(handlerName, handler)) ->
                        cmd.Event
                        |> handler cmd.AggregateId
                        |> AsyncResult.map (fun _ -> handlerName)
                        |> AsyncResult.teeError (fun error ->
                            EventHandlerFailed(cmd.Attempt, cmd.AggregateId, cmd.Event, handlerName, error)
                            |> handleError)
                        |> AsyncResult.setError (handlerName, handler))
                    |> Async.Sequential
                    |> Async.map (Result.extractList >> Tuple.mapFst Set.ofList)

                do!
                    successfulHandlers
                    |> persistSuccessfulHandlers cmd.Event.Id
                    |> AsyncResult.teeError (fun error ->
                        PersistingSuccessfulEventHandlersFailed(cmd.AggregateId, cmd.Event, successfulHandlers, error)
                        |> handleError)
                    |> AsyncResult.ignoreError

                do!
                    match failedHandlers with
                    | [] ->
                        cmd.Event.Id
                        |> markEventAsProcessed
                        |> AsyncResult.teeError (fun err ->
                            (cmd.Event, err) |> MarkingEventAsProcessedFailed |> handleError)
                        |> AsyncResult.ignoreError
                    | failedHandlers ->
                        match cmd.Attempt = maxAttempts with
                        | false ->
                            { cmd with
                                Handlers = failedHandlers |> Map.ofList }
                            |> scheduleRetry
                        | true ->
                            (cmd.Attempt, cmd.AggregateId, cmd.Event, failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> handleError
                            |> Async.retn
            }

        let restoreState =
            readUnprocessedEvents
            >> AsyncResult.teeError (ReadingUnprocessedEventsFailed >> handleError)
            >> AsyncResult.defaultValue []
            >> Async.map (
                List.map (fun (aggregateId, event, successfulHandlers) ->
                    { Attempt = 0
                      Event = event
                      AggregateId = aggregateId
                      Handlers = options.EventHandlerRegistry |> Map.removeKeys successfulHandlers })
            )

        let processor =
            MailboxProcessor<ProcessEvent<'state, 'eventPayload, 'eventHandlingIoError>>.Start(fun inbox ->
                let scheduleRetry = scheduleRetry inbox.Post
                let processEvent = processEvent scheduleRetry

                let rec loop () =
                    async {
                        let! cmd = inbox.Receive()

                        do! { cmd with Attempt = cmd.Attempt + 1 } |> processEvent

                        return! loop ()
                    }

                async {
                    let! state = restoreState ()

                    state |> List.sortBy _.Event.OccurredAt |> List.iter inbox.Post

                    do! loop ()
                })

        member this.Process =
            fun aggregateId events ->
                asyncResult {
                    do! events |> persistEvents aggregateId

                    events
                    |> List.sortBy _.OccurredAt
                    |> List.iter (fun event ->
                        { Attempt = 0
                          Event = event
                          AggregateId = aggregateId
                          Handlers = options.EventHandlerRegistry }
                        |> processor.Post)
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
