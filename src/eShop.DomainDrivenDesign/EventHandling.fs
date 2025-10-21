namespace eShop.DomainDrivenDesign

open System
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open eShop.Prelude
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

[<Measure>]
type event

type EventId = Id<event>

type GenerateEventId = GenerateId<event>

type Event<'payload> =
    { Id: EventId
      Data: 'payload
      OccurredAt: DateTimeOffset }

module Event =
    let inline mapPayload ([<InlineIfLambda>] mapping: 'a -> 'b) (ev: Event<'a>) : Event<'b> =
        { Id = ev.Id
          Data = ev.Data |> mapping
          OccurredAt = ev.OccurredAt }

    // Gets either a union case name of the payload or a type name directly
    let getType (ev: Event<'t>) =
        let payloadType = typeof<'t>

        match FSharpType.IsUnion(payloadType) with
        | true -> FSharpValue.GetUnionFields(ev.Data, payloadType) |> fst |> _.Name
        | false -> payloadType.Name

type EventHandler<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> -> TaskResult<unit, 'ioError>

type EventHandlerName = string

type SuccessfulEventHandlers = EventHandlerName Set

type EventHandlerRegistry<'state, 'eventPayload, 'ioError> =
    Map<EventHandlerName, EventHandler<'state, 'eventPayload, 'ioError>>

type PersistEvents<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> list -> TaskResult<unit, 'ioError>

type ReadUnprocessedEvents<'state, 'eventPayload, 'ioError> =
    unit -> TaskResult<(AggregateId<'state> * Event<'eventPayload> * SuccessfulEventHandlers) list, 'ioError>

type PublishEvents<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> list -> TaskResult<unit, 'ioError>

type PersistSuccessfulEventHandlers<'ioError> = EventId -> SuccessfulEventHandlers -> TaskResult<unit, 'ioError>

type MarkEventAsProcessed<'ioError> = EventId -> TaskResult<unit, 'ioError>

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
              ErrorHandler: EventsProcessorError<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> -> unit }

    let init<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        : EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
        { EventHandlerRegistry = Map.empty
          Retries = ([ 1; 2; 3; 10 ]: float list) |> List.map TimeSpan.FromMinutes
          ErrorHandler = fun _ -> () }

    let withErrorHandler handler options = { options with ErrorHandler = handler }

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
            readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventPayload, 'eventLogIoError>,
            persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventLogIoError>,
            markEventAsProcessed: MarkEventAsProcessed<'eventLogIoError>,
            options: EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        ) =

        let scheduleRetry postCommand cmd =
            task {
                do! options.Retries |> List.item (cmd.Attempt - 1) |> Task.Delay

                return { cmd with Attempt = cmd.Attempt + 1 } |> postCommand
            }

        let maxAttempts = (options.Retries |> List.length) + 1

        let processEvent scheduleRetry cmd =
            task {
                let! successfulHandlers, failedHandlers =
                    cmd.Handlers
                    |> Task.createColdSeq (fun (KeyValue(handlerName, handler)) ->
                        cmd.Event
                        |> handler cmd.AggregateId
                        |> TaskResult.map (fun _ -> handlerName)
                        |> TaskResult.teeError (fun error ->
                            EventHandlerFailed(cmd.Attempt, cmd.AggregateId, cmd.Event, handlerName, error)
                            |> options.ErrorHandler)
                        |> TaskResult.setError (handlerName, handler))
                    |> Task.sequential
                    |> Task.map (Result.extractList >> Tuple.mapFst Set.ofList)

                do!
                    successfulHandlers
                    |> persistSuccessfulHandlers cmd.Event.Id
                    |> TaskResult.teeError (fun error ->
                        PersistingSuccessfulEventHandlersFailed(cmd.AggregateId, cmd.Event, successfulHandlers, error)
                        |> options.ErrorHandler)
                    |> TaskResult.ignoreError

                do!
                    match failedHandlers with
                    | [] ->
                        cmd.Event.Id
                        |> markEventAsProcessed
                        |> TaskResult.teeError (fun err ->
                            (cmd.Event, err) |> MarkingEventAsProcessedFailed |> options.ErrorHandler)
                        |> TaskResult.ignoreError
                    | failedHandlers ->
                        match cmd.Attempt = maxAttempts with
                        | false ->
                            { cmd with
                                Handlers = failedHandlers |> Map.ofList }
                            |> scheduleRetry
                        | true ->
                            (cmd.Attempt, cmd.AggregateId, cmd.Event, failedHandlers |> List.map fst |> Set.ofList)
                            |> MaxEventProcessingRetriesReached
                            |> options.ErrorHandler
                            |> Task.singleton
            }

        let readUnprocessedEvents =
            readUnprocessedEvents
            >> TaskResult.teeError (ReadingUnprocessedEventsFailed >> options.ErrorHandler)
            >> TaskResult.defaultValue []
            >> Task.map (
                List.map (fun (aggregateId, event, successfulHandlers) ->
                    { Attempt = 1
                      Event = event
                      AggregateId = aggregateId
                      Handlers = options.EventHandlerRegistry |> Map.removeKeys successfulHandlers })
            )

        let processor =
            MailboxProcessor<ProcessEvent<'state, 'eventPayload, 'eventHandlingIoError>>.Start(fun inbox ->
                let scheduleRetry = scheduleRetry inbox.Post
                let processEvent = processEvent scheduleRetry

                let rec loop () =
                    task {
                        let! cmd = inbox.Receive()

                        do! cmd |> processEvent

                        return! loop ()
                    }

                task {
                    do!
                        readUnprocessedEvents ()
                        |> Task.map (List.sortBy _.Event.OccurredAt >> List.iter inbox.Post)

                    do! loop ()
                }
                |> Async.AwaitTask)

        member this.Publish: PublishEvents<'state, 'eventPayload, 'eventHandlingIoError> =
            fun aggregateId events ->
                taskResult {
                    events
                    |> List.sortBy _.OccurredAt
                    |> List.iter (fun event ->
                        { Attempt = 1
                          Event = event
                          AggregateId = aggregateId
                          Handlers = options.EventHandlerRegistry }
                        |> processor.Post)
                }

        interface IDisposable with
            member this.Dispose() = processor.Dispose()

    let build
        (readUnprocessedEvents: ReadUnprocessedEvents<'state, 'eventPayload, 'eventLogIoError>)
        (persistSuccessfulHandlers: PersistSuccessfulEventHandlers<'eventLogIoError>)
        (markEventAsProcessed: MarkEventAsProcessed<'eventLogIoError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>)
        =
        new T<_, _, _, _>(readUnprocessedEvents, persistSuccessfulHandlers, markEventAsProcessed, options)

type EventsProcessor<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError> =
    EventsProcessor.T<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>