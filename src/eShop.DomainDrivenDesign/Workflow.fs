namespace eShop.DomainDrivenDesign

open System
open eShop.Prelude
open FsToolkit.ErrorHandling

type UtcNow = DateTimeOffset

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<'state, 'ioError>

type PersistAggregate<'state, 'ioError> = AggregateId<'state> -> 'state -> AsyncResult<unit, 'ioError>

type PublishEvents<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> list -> AsyncResult<unit, 'ioError>

type WorkflowExecutorError<'domainError, 'ioError> =
    | WorkflowError of Either<'domainError, 'ioError>
    | ReadAggregateIoError of 'ioError
    | PersistAggregateIoError of 'ioError
    | PersistEventsIoError of 'ioError
    | PublishEventsIoError of 'ioError

type ExecutableWorkflow<'command, 'state, 'event, 'domainError, 'ioError> =
    UtcNow -> 'state -> 'command -> AsyncResult<'state * 'event list, Either<'domainError, 'ioError>>

type Workflow<'state, 'command, 'domainError, 'ioError> =
    AggregateId<'state> -> 'command -> Async<Result<unit, WorkflowExecutorError<'domainError, 'ioError>>>

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let execute
        (getNow: GetUtcNow)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (publishEvents: PublishEvents<'state, 'event, 'ioError>)
        (workflow: ExecutableWorkflow<'command, 'state, 'event, 'domainError, 'ioError>)
        : Workflow<'state, 'command, 'domainError, 'ioError> =
        fun aggregateId command ->
            asyncResult {
                let now = getNow ()

                let inline mapError ctor = AsyncResult.mapError ctor

                let! state = aggregateId |> readAggregate |> mapError ReadAggregateIoError

                let! newState, events = command |> workflow now state |> mapError WorkflowError

                do! newState |> persistAggregate aggregateId |> mapError PersistAggregateIoError

                do!
                    events
                    |> List.map (Event.createNew now)
                    |> publishEvents aggregateId
                    |> mapError PublishEventsIoError
            }
