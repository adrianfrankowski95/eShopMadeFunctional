namespace eShop.DomainDrivenDesign

open System
open FsToolkit.ErrorHandling
open eShop.Prelude

type UtcNow = DateTimeOffset

type GetUtcNow = unit -> UtcNow

type ReadAggregate<'st, 'ioError> = AggregateId<'st> -> TaskResult<'st, 'ioError>

type PersistAggregate<'st, 'ioError> = AggregateId<'st> -> 'st -> TaskResult<unit, 'ioError>

type WorkflowExecutionResult<'a, 'err, 'ioErr> = TaskResult<'a, Either<'err, 'ioErr>>

[<RequireQualifiedAccess>]
module WorkflowExecutor =
    let execute
        (aggregateId: AggregateId<'st>)
        (getNow: GetUtcNow)
        (generateEventId: GenerateEventId)
        (readAggregate: ReadAggregate<'st, 'ioErr>)
        (persistAggregate: PersistAggregate<'st, 'ioErr>)
        (persistEvents: PersistEvents<'st, 'ev, 'ioErr>)
        (workflow: Workflow<'st, 'ev, 'err, 'ioErr, 'a>)
        : WorkflowExecutionResult<'a, 'err, 'ioErr> =
        taskResult {
            let inline toIoError x = x |> TaskResult.mapError Right

            let now = getNow ()

            let! st0 = aggregateId |> readAggregate |> toIoError

            let! st, ev, x = st0 |> Workflow.run workflow

            do! st |> persistAggregate aggregateId |> toIoError

            do!
                ev
                |> List.map (fun e ->
                    { Id = generateEventId ()
                      Data = e
                      OccurredAt = now })
                |> persistEvents aggregateId
                |> toIoError

            return x
        }

    let executeNew generateOrderId getNow generateEventId readAggregate persistAggregate persistEvents workflow =
        let id = generateOrderId ()

        execute id getNow generateEventId readAggregate persistAggregate persistEvents workflow
        |> TaskResult.map (fun x -> id, x)
