namespace eShop.DomainDrivenDesign

open eShop.Prelude
open FsToolkit.ErrorHandling

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<'state, 'ioError>

type PersistAggregate<'state, 'ioError> = AggregateId<'state> -> 'state -> AsyncResult<unit, 'ioError>

type PersistEvents<'state, 'eventId, 'event, 'ioError> =
    AggregateId<'state> -> Event<'event> list -> AsyncResult<('eventId * Event<'event>) list, 'ioError>

type PublishEvents<'state, 'eventId, 'event, 'ioError> =
    AggregateId<'state> -> ('eventId * Event<'event>) list -> AsyncResult<unit, 'ioError>

type WorkflowExecutorError<'domainError, 'workflowIoError, 'persistenceIoError, 'publishEventsIoError> =
    | WorkflowError of Either<'domainError, 'workflowIoError>
    | ReadAggregateIoError of 'persistenceIoError
    | PersistAggregateIoError of 'persistenceIoError
    | PersistEventsIoError of 'persistenceIoError
    | PublishEventsIoError of 'publishEventsIoError

type WorkflowExecution<'state, 'command, 'domainError, 'workflowIoError, 'persistenceIoError, 'publishEventsIoError> =
    AggregateId<'state>
        -> 'command
        -> Async<
            Result<
                unit,
                WorkflowExecutorError<'domainError, 'workflowIoError, 'persistenceIoError, 'publishEventsIoError>
             >
         >

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let execute
        (getNow: GetUtcNow)
        (readAggregate: ReadAggregate<'state, 'persistenceIoError>)
        (persistAggregate: PersistAggregate<'state, 'persistenceIoError>)
        (persistEvents: PersistEvents<'state, 'eventId, 'event, 'persistenceIoError>)
        (publishEvents: PublishEvents<'state, 'eventId, 'event, 'publishEventsIoError>)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'workflowIoError>)
        : WorkflowExecution<'state, 'command, 'domainError, 'workflowIoError, 'persistenceIoError, 'publishEventsIoError> =
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

                do! eventsWithIds |> publishEvents aggregateId |> mapError PublishEventsIoError
            }
