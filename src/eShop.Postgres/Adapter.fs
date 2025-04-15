namespace eShop.Postgres

open System
open System.Data.Common
open System.Threading
open DbUp
open DbUp.Engine

type GetDbConnection = unit -> DbConnection

type DbConnectionString = string

type DbSchema = DbSchema of string

type SqlIoError =
    | SerializationError of exn
    | DeserializationError of exn
    | SqlException of exn
    | InvalidData of string

[<RequireQualifiedAccess>]
type SqlSession =
    | Sustained of DbTransaction
    | Standalone of GetDbConnection

[<RequireQualifiedAccess>]
module Postgres =
    let private handleScriptExecutionResult name (result: DatabaseUpgradeResult) =
        match result.Successful with
        | true ->
            Console.ForegroundColor <- ConsoleColor.Green

            $"""Successfully initialized %s{name} database. Executed scripts: %s{result.Scripts |> Seq.map _.Name |> String.concat ", "}"""
            |> Console.WriteLine
            |> Console.ResetColor
        | false ->
            Console.ForegroundColor <- ConsoleColor.Red

            $"""Failed to initialize %s{name} database, error: %s{result.Error.Message}"""
            |> Console.WriteLine
            |> Console.ResetColor

    let executeScripts (name: string) (path: string) (DbSchema schema) (connectionString: DbConnectionString) =
        let rec ensureDb retries =
            try
                EnsureDatabase.For.PostgresqlDatabase(connectionString)
            with e ->
                match retries with
                | 0 ->
                    $"Cannot initialize %s{name} - it does not exist. Error: {e.Message}. ConnectionString: %s{connectionString}"
                    |> failwith
                | _ ->
                    Thread.Sleep(3000)
                    ensureDb (retries - 1)

        let executeScripts =
            DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsFromFileSystem(path)
                .WithVariable("Schema", schema)
                .WithTransactionPerScript()
                .JournalToPostgresqlTable(null, "Migrations")
                .LogToConsole()
                .Build()
                .PerformUpgrade

        ensureDb 5 |> executeScripts |> handleScriptExecutionResult name
