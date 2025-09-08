namespace eShop.DomainDrivenDesign

open System
open eShop.ConstrainedTypes
open eShop.Prelude
open FsToolkit.ErrorHandling

type UtcNow = DateTimeOffset

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<'state, 'ioError>

type PersistAggregate<'state, 'ioError> = AggregateId<'state> -> 'state -> AsyncResult<unit, 'ioError>

type Workflow<'command, 'state, 'event, 'domainError, 'ioError> =
    UtcNow -> 'state -> 'command -> AsyncResult<'state * 'event list, Either<'domainError, 'ioError>>

type WorkflowExecutionError<'state, 'domainError, 'ioError> =
    | DomainError of 'domainError
    | AggregateAlreadyExists of AggregateId<'state>
    | IoError of 'ioError

type WorkflowResult<'state, 'event, 'domainError, 'ioError> =
    AsyncResult<AggregateId<'state> * Event<'event> list, WorkflowExecutionError<'state, 'domainError, 'ioError>>

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let executeActionForExistingAggregate
        (aggregateId: AggregateId<'state>)
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (persistEvents: PersistEvents<'state, 'event, 'ioError>)
        (action: AggregateAction<'state, 'event, 'domainError>)
        : WorkflowResult<'state, 'event, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()

            let! state = aggregateId |> readAggregate |> mapError IoError

            let! newState, events =
                state
                |> action
                |> Result.map (
                    Tuple.mapSnd (
                        List.map (fun payload ->
                            { Id = generateEventId ()
                              Data = payload
                              OccurredAt = now })
                    )
                )
                |> Result.mapError DomainError

            do! newState |> persistAggregate aggregateId |> mapError IoError

            do! events |> persistEvents aggregateId |> mapError IoError

            return aggregateId, events
        }

    let executeActionForNewAggregate
        (initialState: 'state)
        (generateAggregateId: GenerateAggregateId<'state>)
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (persistEvents: PersistEvents<'state, 'event, 'ioError>)
        (action: AggregateAction<'state, 'event, 'domainError>)
        : WorkflowResult<'state, 'event, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()
            let aggregateId = generateAggregateId ()

            let! existingState = aggregateId |> readAggregate |> mapError IoError

            do!
                aggregateId
                |> AggregateAlreadyExists
                |> Result.requireEqual initialState existingState

            let! newState, events =
                initialState
                |> action
                |> Result.map (
                    Tuple.mapSnd (
                        List.map (fun payload ->
                            { Id = generateEventId ()
                              Data = payload
                              OccurredAt = now })
                    )
                )
                |> Result.mapError DomainError

            do! newState |> persistAggregate aggregateId |> mapError IoError

            do! events |> persistEvents aggregateId |> mapError IoError

            return aggregateId, events
        }

    let executeForExistingAggregate
        (aggregateId: AggregateId<'state>)
        (command: 'command)
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (persistEvents: PersistEvents<'state, 'event, 'ioError>)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'ioError>)
        : WorkflowResult<'state, 'event, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()

            let! state = aggregateId |> readAggregate |> mapError IoError

            let! newState, events =
                command
                |> workflow now state
                |> AsyncResult.map (
                    Tuple.mapSnd (
                        List.map (fun payload ->
                            { Id = generateEventId ()
                              Data = payload
                              OccurredAt = now })
                    )
                )
                |> mapError (Either.either DomainError IoError >> Either.collapse)

            do! newState |> persistAggregate aggregateId |> mapError IoError

            do! events |> persistEvents aggregateId |> mapError IoError

            return aggregateId, events
        }

    let executeForNewAggregate
        (initialState: 'state)
        (command: 'command)
        (generateAggregateId: GenerateAggregateId<'state>)
        (getNow: GetUtcNow)
        (generateEventId: GenerateId<eventId>)
        (readAggregate: ReadAggregate<'state, 'ioError>)
        (persistAggregate: PersistAggregate<'state, 'ioError>)
        (persistEvents: PersistEvents<'state, 'event, 'ioError>)
        (workflow: Workflow<'command, 'state, 'event, 'domainError, 'ioError>)
        : WorkflowResult<'state, 'event, 'domainError, 'ioError> =
        asyncResult {
            let inline mapError ctor = AsyncResult.mapError ctor

            let now = getNow ()
            let aggregateId = generateAggregateId ()

            let! existingState = aggregateId |> readAggregate |> mapError IoError

            do!
                aggregateId
                |> AggregateAlreadyExists
                |> Result.requireEqual initialState existingState

            let! newState, events =
                command
                |> workflow now initialState
                |> AsyncResult.map (
                    Tuple.mapSnd (
                        List.map (fun payload ->
                            { Id = generateEventId ()
                              Data = payload
                              OccurredAt = now })
                    )
                )
                |> mapError (Either.either DomainError IoError >> Either.collapse)

            do! newState |> persistAggregate aggregateId |> mapError IoError

            do! events |> persistEvents aggregateId |> mapError IoError

            return aggregateId, events
        }
