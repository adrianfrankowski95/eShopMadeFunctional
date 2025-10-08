namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data
open System.Data.Common
open System.Threading.Tasks
open eShop.DomainDrivenDesign
open eShop.Postgres
open eShop.Prelude
open eShop
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module TransactionalWorkflowExecutor =
    type private Delay = TimeSpan

    type Options<'ioErr> =
        private
            { GetDbConnection: GetDbConnection
              IsolationLevel: IsolationLevel
              Retries: Delay list
              MapIoError: SqlIoError -> 'ioErr }

    let init getDbConnection =
        { GetDbConnection = getDbConnection
          IsolationLevel = IsolationLevel.ReadCommitted
          Retries = [ (1: float); 3; 5; 15 ] |> List.map TimeSpan.FromSeconds
          MapIoError = id }

    let withRetries retries options = { options with Retries = retries }

    let withIsolationLevel isolationLevel options =
        { options with
            IsolationLevel = isolationLevel }

    let withIoErrorMapping f options =
        { GetDbConnection = options.GetDbConnection
          IsolationLevel = options.IsolationLevel
          MapIoError = f
          Retries = options.Retries }

    let rec private executeInTransaction
        (options: Options<'ioErr>)
        (workflow: DbTransaction -> WorkflowExecutionResult<'a, 'err, 'ioErr>)
        : WorkflowExecutionResult<'a, 'err, 'ioErr> =
        taskResult {
            use connection = options.GetDbConnection()
            do! connection.OpenAsync()

            let! transaction = connection.BeginTransactionAsync(options.IsolationLevel)

            let rollback =
                fun _ ->
                    task {
                        do! transaction.RollbackAsync()
                        do! connection.CloseAsync()
                    }

            let! result = transaction |> workflow |> TaskResult.teeErrorAsync rollback

            do!
                transaction.CommitAsync >> Task.ofUnit
                |> Prelude.TaskResult.catch (SqlIoError.TransactionCommitError >> options.MapIoError >> Right)
                |> TaskResult.teeErrorAsync rollback

            return result
        }
        |> TaskResult.orElseWith (fun error ->
            match options.Retries with
            | [] -> error |> TaskResult.error
            | head :: tail ->
                taskResult {
                    do! head |> Task.Delay
                    return! executeInTransaction { options with Retries = tail } workflow
                })

    let execute
        (getNow: GetUtcNow)
        (generateEventId: GenerateEventId)
        (readAggregate: DbTransaction -> ReadAggregate<'st, 'ioErr>)
        (persistAggregate: DbTransaction -> PersistAggregate<'st, 'ioErr>)
        (persistEvents: DbTransaction -> PersistEvents<'st, 'ev, 'ioErr>)
        (workflow: DbTransaction -> Workflow<'st, 'ev, 'err, 'ioErr, 'a>)
        (options: Options<'ioErr>)
        (aggregateId: AggregateId<'st>)
        =
        reader {
            let! readAggregate = readAggregate
            let! persistAggregate = persistAggregate
            let! persistEvents = persistEvents
            let! workflow = workflow

            return
                WorkflowExecutor.execute
                    aggregateId
                    getNow
                    generateEventId
                    readAggregate
                    persistAggregate
                    persistEvents
                    workflow
        }
        |> executeInTransaction options

    let inline andThen action result =
        result
        |> TaskResult.bind (fun (aggregateId, events) -> events |> action aggregateId |> TaskResult.mapError Right)
