[<RequireQualifiedAccess>]
module eShop.Postgres.TransactionalExecutor

open System
open System.Threading.Tasks
open System.Transactions
open eShop.Postgres
open FsToolkit.ErrorHandling

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

let rec execute (options: Options) action =
    taskResult {
        use transaction =
            new TransactionScope(
                TransactionScopeOption.Required,
                TransactionOptions(IsolationLevel = options.IsolationLevel),
                TransactionScopeAsyncFlowOption.Enabled
            )

        use connection = options.GetDbConnection()
        do! connection.OpenAsync()

        let! result = connection |> action

        // "By-design" all remaining SqlConnections in the pool will be polluted with selected Isolation Level, we need to reset it back to default
        if options.IsolationLevel <> IsolationLevel.ReadCommitted then
            use cmd =
                connection.CreateCommand(CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED")

            do! cmd.ExecuteNonQueryAsync() |> Task.ignore

        transaction.Complete()

        return result
    }
    |> TaskResult.orElseWith (fun error ->
        match options.Retries with
        | [] -> error |> TaskResult.error
        | head :: tail ->
            taskResult {
                do! head |> Task.Delay
                return! execute { options with Retries = tail } action
            })
