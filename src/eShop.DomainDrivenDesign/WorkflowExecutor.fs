namespace eShop.DomainDrivenDesign

open eShop.Prelude
open FsToolkit.ErrorHandling

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<'state, 'ioError>

type PersistAggregate<'state, 'ioError> = AggregateId<'state> -> 'state -> AsyncResult<unit, 'ioError>

type PersistEvents<'state, 'eventId, 'event, 'ioError> =
    AggregateId<'state> -> Event<'event> list -> AsyncResult<('eventId * Event<'event>) list, 'ioError>

type PublishEvents<'eventId, 'event, 'ioError> = ('eventId * Event<'event>) list -> AsyncResult<unit, 'ioError>

type WorkflowExecutorError<'domainError, 'workflowIoError, 'readAggregateIoError, 'persistAggregateIoError, 'persistEventsIoError, 'publishEventsIoError>
    =
    | WorkflowError of Either<'domainError, 'workflowIoError>
    | ReadAggregateIoError of 'readAggregateIoError
    | PersistAggregateIoError of 'persistAggregateIoError
    | PersistEventsIoError of 'persistEventsIoError
    | PublishEventsIoError of 'publishEventsIoError

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let execute
        (getNow: GetUtcNow)
        (readAggregate: ReadAggregate<'state, 'readAggregateIoError>)
        (persistAggregate: PersistAggregate<'state, 'persistAggregateIoError>)
        (persistEvents: PersistEvents<'state, 'eventId, 'event, 'persistEventsIoError>)
        (publishEvents: PublishEvents<'eventId, 'event, 'publishEventsIoError>)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'ioError>)
        =
        fun aggregateId command ->
            asyncResult {
                let now = getNow ()

                let inline mapError ctor = AsyncResult.mapError ctor

                let! state = aggregateId |> readAggregate |> mapError ReadAggregateIoError

                let! newState, events = command |> workflow now state |> mapError WorkflowError

                do! newState |> persistAggregate aggregateId |> mapError PersistAggregateIoError

                let! eventsWithIds =
                    events
                    |> List.map (fun ev -> { Data = ev; OccurredAt = now })
                    |> persistEvents aggregateId
                    |> mapError PersistEventsIoError

                do! eventsWithIds |> publishEvents |> mapError PublishEventsIoError
            }
