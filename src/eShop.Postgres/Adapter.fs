namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open DbUp
open DbUp.Engine
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

type GetDbConnection = unit -> DbConnection

type DbConnectionString = string

type DbSchema = DbSchema of string

type SqlIoError =
    | SerializationException of exn
    | DeserializationException of exn
    | SqlException of exn

type SqlSession =
    | WithTransaction of DbTransaction
    | WithoutTransaction of DbConnection

type DbScript = private DbScript of (DbConnectionString -> unit)

[<RequireQualifiedAccess>]
module Postgres =
    let private handleScriptExecutionResult dbName (result: DatabaseUpgradeResult) =
        match result.Successful with
        | true ->
            Console.ForegroundColor <- ConsoleColor.Green

            $"""Successfully initialized %s{dbName} database. Executed scripts: %s{result.Scripts |> Seq.map _.Name |> String.concat ", "}"""
            |> Console.WriteLine
            |> Console.ResetColor
        | false ->
            Console.ForegroundColor <- ConsoleColor.Red

            $"""Failed to initialize %s{dbName} database, error: %s{result.Error.Message}. Error script: %s{result.ErrorScript.Name}"""
            |> Console.WriteLine
            |> Console.ResetColor

    let createScript (dbName: string) (path: string) (DbSchema schema) =
        fun (connectionString: DbConnectionString) ->
            let rec ensureDb retries =
                try
                    EnsureDatabase.For.PostgresqlDatabase(connectionString)
                with _ ->
                    match retries with
                    | 0 -> $"Cannot initialize %s{dbName} DB - it does not exist" |> failwith
                    | _ ->
                        Thread.Sleep(3000)
                        ensureDb (retries - 1)

            let executeScripts =
                DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScriptsFromFileSystem(path)
                    .WithVariable("Schema", schema)
                    .WithTransactionPerScript()
                    .LogToConsole()
                    .Build()
                    .PerformUpgrade

            ensureDb 5 |> executeScripts |> handleScriptExecutionResult dbName
        |> DbScript

module DependencyInjection =
    let addDbSchema (schema: DbSchema) (services: IServiceCollection) = services.AddSingleton<DbSchema>(schema)

    let addDbScript (DbScript script) (services: IServiceCollection) =
        services.AddHostedService(fun sp ->
            let connectionString = sp.GetRequiredService<DbConnection>().ConnectionString

            { new IHostedService with
                member this.StartAsync(cancellationToken) =
                    connectionString |> script
                    Task.CompletedTask

                member this.StopAsync(cancellationToken) = this.StopAsync(cancellationToken) })
