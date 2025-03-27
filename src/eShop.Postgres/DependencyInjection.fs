module eShop.Postgres.DependencyInjection

open System.Data.Common
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql

type DbScriptName = string
type DbScriptRelativePath = string

type IServiceCollection with
    member this.AddPostgres =
        fun connectionString dbSchema (dbScripts: Map<DbScriptName, DbScriptRelativePath>) (env: IHostEnvironment) ->
            Dapper.TypeHandlers.register ()

            dbScripts
            |> Map.map Postgres.executeScripts
            |> Map.values
            |> Seq.map ((|>) dbSchema >> ((|>) connectionString))
            |> Seq.toList
            |> ignore

            this
                .AddSingleton<NpgsqlDataSource>(fun sp ->
                    sp.GetRequiredService<JsonSerializerOptions>()
                    |> NpgsqlDataSourceBuilder(connectionString)
                        .EnableParameterLogging(env.IsDevelopment())
                        .EnableDynamicJson()
                        .ConfigureJsonOptions
                    |> _.Build())
                .AddTransient<DbConnection>(_.GetRequiredService<NpgsqlDataSource>().CreateConnection())
                .AddSingleton<DbSchema>(dbSchema)
