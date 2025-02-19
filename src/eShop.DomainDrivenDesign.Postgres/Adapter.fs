namespace eShop.DomainDrivenDesign.Postgres

open System.Data.Common
open System.Threading
open DbUp

type GetDbConnection = unit -> DbConnection

type DbConnectionString = string

type DbSchema = string

type SqlIoError =
    | SerializationException of exn
    | DeserializationException of exn
    | SqlException of exn

type SqlConnection =
    | WithTransaction of DbTransaction
    | WithoutTransaction of DbConnection

[<RequireQualifiedAccess>]
module Db =
    let init (scriptsPath: string) (schema: DbSchema) (connectionString: DbConnectionString) =
        let rec ensureDb retries =
            try
                EnsureDatabase.For.PostgresqlDatabase(connectionString)
            with _ ->
                match retries with
                | 0 -> "Cannot initialize DB - it does not exist" |> failwith
                | _ ->
                    Thread.Sleep(3000)
                    ensureDb (retries - 1)

        let invokeScripts () =
            DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsFromFileSystem(scriptsPath)
                .WithVariable("Schema", schema)
                .WithTransactionPerScript()
                .LogToConsole()
                .Build()
                .PerformUpgrade()

        ensureDb 5
        invokeScripts ()