module eShop.Postgres.DependencyInjection

open System.Data.Common
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open Microsoft.AspNetCore.Hosting

type ScriptName = string
type ScriptRelativePath = string

type IServiceCollection with
    member this.AddPostgres =
        fun connectionString dbSchema (dbScripts: Map<ScriptName, ScriptRelativePath>) (env: IWebHostEnvironment) ->
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
