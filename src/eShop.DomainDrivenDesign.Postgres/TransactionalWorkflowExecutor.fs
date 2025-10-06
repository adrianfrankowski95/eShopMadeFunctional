namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data
open System.Data.Common
open System.Threading.Tasks
open eShop.DomainDrivenDesign
open eShop.Postgres
open eShop.Prelude
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module TransactionalWorkflowExecutor =
    type private Delay = TimeSpan

    type Options =
        private
            { GetDbConnection: GetDbConnection
              IsolationLevel: IsolationLevel
              Retries: Delay list }

    let init getDbConnection =
        { GetDbConnection = getDbConnection
          IsolationLevel = IsolationLevel.ReadCommitted
          Retries = [ (1: float); 3; 5; 15 ] |> List.map TimeSpan.FromSeconds }

    let withRetries retries options = { options with Retries = retries }

    let withIsolationLevel isolationLevel options =
        { options with
            IsolationLevel = isolationLevel }

    let execute
        (workflow: DbTransaction -> WorkflowResult<'state, 'event, 'domainError, 'ioError>)
        (mapSqlIoError: SqlIoError -> 'ioError)
        (options: Options)
        =
        let rec executeInTransaction
            (workflow: DbTransaction -> WorkflowResult<'state, 'event, 'domainError, 'ioError>)
            (retries: Delay list)
            =
            taskResult {
                use connection = options.GetDbConnection()
                do! connection.OpenAsync()

                let! transaction = connection.BeginTransactionAsync(options.IsolationLevel)

                let! result =
                    transaction
                    |> workflow
                    |> TaskResult.teeErrorAsync (fun _ -> transaction.RollbackAsync())

                try
                    do! transaction.CommitAsync()
                    do! connection.CloseAsync()
                with e ->
                    do! transaction.RollbackAsync()
                    do! connection.CloseAsync()

                    return!
                        e
                        |> SqlIoError.TransactionCommitError
                        |> mapSqlIoError
                        |> WorkflowExecutionError.IoError
                        |> Error

                return result
            }
            |> TaskResult.orElseWith (fun error ->
                match retries with
                | [] -> error |> TaskResult.error
                | head :: tail ->
                    taskResult {
                        do! head |> Task.Delay
                        return! tail |> executeInTransaction workflow
                    })

        executeInTransaction workflow options.Retries

    let inline andThen action result =
        result
        |> TaskResult.bind (fun (aggregateId, events) ->
            events
            |> action aggregateId
            |> TaskResult.mapError WorkflowExecutionError.IoError)
