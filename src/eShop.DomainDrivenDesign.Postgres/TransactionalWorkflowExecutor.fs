[<RequireQualifiedAccess>]
module eShop.DomainDrivenDesign.Postgres

open System
open System.Data
open System.Data.Common
open eShop.Prelude
open FsToolkit.ErrorHandling

type GetDbConnection = unit -> DbConnection

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
          Retries = ([ 1; 3; 5; 15 ]: float list) |> List.map TimeSpan.FromSeconds }

    let withRetries retries options = { options with Retries = retries }

    let withIsolationLevel isolationLevel options =
        { options with
            IsolationLevel = isolationLevel }

    let execute workflow (options: Options) =
        fun state command ->
            let rec executeInTransaction workflow (retries: Delay list) =
                asyncResult {
                    let connection = options.GetDbConnection()
                    do! connection.OpenAsync()

                    let! transaction = connection.BeginTransactionAsync(options.IsolationLevel).AsTask()

                    return!
                        command
                        |> workflow transaction state
                        |> AsyncResult.teeAsync (transaction.CommitAsync >> Async.AwaitTask)
                        |> AsyncResult.teeErrorAsync (transaction.RollbackAsync >> Async.AwaitTask)
                        |> AsyncResult.teeUnitAsync (connection.CloseAsync >> Async.AwaitTask)
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
