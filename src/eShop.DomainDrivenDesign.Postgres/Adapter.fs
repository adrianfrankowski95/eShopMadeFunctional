namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Threading
open DbUp
open DbUp.Engine

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
    let private handleScriptExecutionResult dbName (result: DatabaseUpgradeResult) =
        match result.Successful with
        | true ->
            Console.ForegroundColor <- ConsoleColor.Green

            $"""Successfully initialized %s{dbName} database. Executed scripts: %s{result.Scripts |> Seq.map _.Name |> String.concat ", "}"""
            |> Console.WriteLine

            Console.ResetColor()
        | false ->
            Console.ForegroundColor <- ConsoleColor.Red

            $"""Failed to initialize %s{dbName} database, error: %s{result.Error.Message}. Error script: %s{result.ErrorScript.Name}"""
            |> Console.WriteLine

            Console.ResetColor()

    let init (dbName: string) (scriptsPath: string) (schema: DbSchema) (connectionString: DbConnectionString) =
        let rec ensureDb retries =
            try
                EnsureDatabase.For.PostgresqlDatabase(connectionString)
            with _ ->
                match retries with
                | 0 -> $"Cannot initialize %s{dbName} DB - it does not exist" |> failwith
                | _ ->
                    Thread.Sleep(3000)
                    ensureDb (retries - 1)

        let executeScripts () =
            DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsFromFileSystem(scriptsPath)
                .WithVariable("Schema", schema)
                .WithTransactionPerScript()
                .LogToConsole()
                .Build()
                .PerformUpgrade()

        ensureDb 5
        executeScripts () |> handleScriptExecutionResult dbName
