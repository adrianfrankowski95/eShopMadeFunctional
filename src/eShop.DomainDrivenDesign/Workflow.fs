namespace eShop.DomainDrivenDesign

open System
open eShop.ConstrainedTypes
open eShop.Prelude
open FsToolkit.ErrorHandling

type UtcNow = DateTimeOffset

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<'state, 'ioError>

type PersistAggregate<'state, 'ioError> = AggregateId<'state> -> 'state -> AsyncResult<unit, 'ioError>

type PublishEvents<'state, 'eventPayload, 'ioError> =
    AggregateId<'state> -> Event<'eventPayload> list -> AsyncResult<unit, 'ioError>

type Workflow<'command, 'state, 'event, 'domainError, 'ioError> =
    UtcNow -> 'state -> 'command -> AsyncResult<'state * 'event list, Either<'domainError, 'ioError>>

type WorkflowExecutionError<'state, 'domainError, 'ioError> =
    | WorkflowError of Either<'domainError, 'ioError>
    | AggregateAlreadyExists of AggregateId<'state>
    | ReadAggregateIoError of 'ioError
    | PersistAggregateIoError of 'ioError
    | PersistEventsIoError of 'ioError
    | PublishEventsIoError of 'ioError

type WorkflowResult<'state, 'domainError, 'ioError> =
    AsyncResult<unit, WorkflowExecutionError<'state, 'domainError, 'ioError>>

[<RequireQualifiedAccess>]
module Workflow =
    let runForAggregate
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (publishEvents: PublishEvents<'state, 'event, 'ioError>)
        (aggregateId: AggregateId<'state>)
        (command: 'command)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'ioError>)
        : WorkflowResult<'state, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()

            let! state = aggregateId |> readAggregate |> mapError ReadAggregateIoError

            let! newState, events = command |> workflow now state |> mapError WorkflowError

            do! newState |> persistAggregate aggregateId |> mapError PersistAggregateIoError

            do!
                events
                |> List.map (fun payload ->
                    { Id = generateEventId ()
                      Data = payload
                      OccurredAt = now })
                |> publishEvents aggregateId
                |> mapError PublishEventsIoError
        }

    let runForNewAggregate
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (generateAggregateId: GenerateAggregateId<'state>)
        (initialState: 'state)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (publishEvents: PublishEvents<'state, 'event, 'ioError>)
        (command: 'command)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'ioError>)
        : WorkflowResult<'state, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()
            let aggregateId = generateAggregateId ()

            let! existingState = aggregateId |> readAggregate |> mapError ReadAggregateIoError

            do!
                aggregateId
                |> AggregateAlreadyExists
                |> Result.requireEqual initialState existingState

            let! newState, events = command |> workflow now initialState |> mapError WorkflowError

            do! newState |> persistAggregate aggregateId |> mapError PersistAggregateIoError

            do!
                events
                |> List.map (fun payload ->
                    { Id = generateEventId ()
                      Data = payload
                      OccurredAt = now })
                |> publishEvents aggregateId
                |> mapError PublishEventsIoError
        }
