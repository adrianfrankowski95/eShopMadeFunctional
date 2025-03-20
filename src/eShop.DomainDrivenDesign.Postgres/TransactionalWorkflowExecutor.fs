namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data
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

    let execute workflow (options: Options) : WorkflowExecution<_, _, _, _, _, _> =
        fun state command ->
            let rec executeInTransaction workflow (retries: Delay list) =
                asyncResult {
                    let connection = options.GetDbConnection()
                    do! connection.OpenAsync()

                    let! transaction = connection.BeginTransactionAsync(options.IsolationLevel).AsTask()

                    return!
                        command
                        |> workflow transaction state
                        |> AsyncResult.teeAsync (fun _ -> transaction.CommitAsync() |> Async.AwaitTask)
                        |> AsyncResult.teeErrorAsync (fun _ -> transaction.RollbackAsync() |> Async.AwaitTask)
                        |> AsyncResult.teeAnyAsync (connection.CloseAsync >> Async.AwaitTask)
                        |> AsyncResult.orElseWith (fun error ->
                            match retries with
                            | [] -> error |> AsyncResult.error
                            | head :: tail ->
                                asyncResult {
                                    do! head |> Async.Sleep
                                    return! tail |> executeInTransaction workflow
                                })
                }

            executeInTransaction workflow options.Retries
