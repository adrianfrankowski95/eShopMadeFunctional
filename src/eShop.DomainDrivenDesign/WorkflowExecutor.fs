namespace eShop.DomainDrivenDesign

open eShop.Prelude
open FsToolkit.ErrorHandling

type ReadAggregate<'state, 'ioError> = AggregateId<'state> -> AsyncResult<Aggregate<'state> option, 'ioError>

type PersistAggregate<'state, 'ioError> = Aggregate<'state> -> AsyncResult<unit, 'ioError>

type PersistEvents<'eventId, 'event, 'ioError> =
    Event<'event> list -> AsyncResult<('eventId * Event<'event>) list, 'ioError>

type PublishEvents<'eventId, 'event, 'ioError> = ('eventId * Event<'event>) list -> AsyncResult<unit, 'ioError>

type WorkflowExecutorError<'domainError, 'readAggregateIoError, 'persistAggregateIoError, 'persistEventsIoError, 'publishEventsIoError>
    =
    | DomainError of 'domainError
    | ReadAggregateIoError of 'readAggregateIoError
    | PersistAggregateIoError of 'persistAggregateIoError
    | PersistEventsIoError of 'persistEventsIoError
    | PublishEventsIoError of 'publishEventsIoError

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let execute
        (readAggregate: ReadAggregate<'state, 'readAggregateIoError>)
        (persistAggregate: PersistAggregate<'state, 'persistAggregateIoError>)
        (persistEvents: PersistEvents<'eventId, 'event, 'persistEventsIoError>)
        (publishEvents: PublishEvents<'eventId, 'event, 'publishEventsIoError>)
        (workflow: Workflow<'command, 'state, 'event, 'domainError>)
        =
        fun aggregateId command ->
            asyncResult {
                let inline mapError ctor = AsyncResult.mapError ctor

                let! maybeAggregate =
                    aggregateId
                    |> readAggregate
                    |> AsyncResult.map (MaybeAggregate.ofOption aggregateId)
                    |> mapError ReadAggregateIoError

                let! newState, events = command |> workflow maybeAggregate |> mapError DomainError

                do! (aggregateId, newState) |> persistAggregate |> mapError PersistAggregateIoError

                let! eventsWithIds = events |> persistEvents |> mapError PersistEventsIoError

                do! eventsWithIds |> publishEvents |> mapError PublishEventsIoError
            }
