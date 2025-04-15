module eShop.Postgres.DependencyInjection

open System
open System.Data.Common
open System.Runtime.CompilerServices
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql

type DbScriptName = string
type DbScriptRelativePath = string

type Extensions =
    [<Extension>]
    static member AddPostgres
        (
            services: IServiceCollection,
            connectionString,
            dbSchema,
            dbScripts: Map<DbScriptName, DbScriptRelativePath>,
            env: IHostEnvironment
        ) =
        Dapper.TypeHandlers.register ()
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores <- true

        dbScripts
        |> Map.map Postgres.executeScripts
        |> Map.values
        |> Seq.map ((|>) dbSchema >> ((|>) connectionString))
        |> Seq.toList
        |> ignore

        services
            .AddSingleton<NpgsqlDataSource>(fun sp ->
                let isDevelopment = env.IsDevelopment()
                let jsonOptions = sp.GetRequiredService<JsonSerializerOptions>()

                let builder = NpgsqlDataSourceBuilder(connectionString)
                builder.ConnectionStringBuilder.LogParameters <- isDevelopment
                builder.ConnectionStringBuilder.IncludeErrorDetail <- isDevelopment

                builder
                    .EnableParameterLogging(isDevelopment)
                    .ConfigureJsonOptions(jsonOptions)
                    .Build())
            .AddTransient<GetDbConnection>(
                Func<IServiceProvider, GetDbConnection>(fun sp ->
                    fun () -> sp.GetRequiredService<NpgsqlDataSource>().CreateConnection() :> DbConnection)
            )
            .AddSingleton<DbSchema>(dbSchema)
