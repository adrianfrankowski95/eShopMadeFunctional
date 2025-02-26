[<RequireQualifiedAccess>]
module Ordering.API.Configuration

open System.Data.Common
open System.IO
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open eShop.Postgres
open eShop.ServiceDefaults
open eShop.EventBusRabbitMQ
open eShop.Prelude
open Ordering.API.WebApp
open System.Text.Json.Serialization
open FSharp.Data.LiteralProviders

let private dbScriptsMap =
    let getRelativeDirectoryPath path =
        Path.GetRelativePath(__SOURCE_DIRECTORY__, path) |> Path.GetDirectoryName

    Map.empty
    |> Map.add
        "EventProcessing"
        TextFile<
            "../../eShop.DomainDrivenDesign.Postgres/dbinit/001_Create_EventProcessingLog_Table.sql",
            EnsureExists=true
         >.Path
    |> Map.add
        "Ordering"
        TextFile<"../Ordering.Adapters.Postgres/dbinit/001_Create_Ordering_Tables.sql", EnsureExists=true>.Path
    |> Map.mapValues getRelativeDirectoryPath

let private configureDb (config: IConfiguration) (env: IWebHostEnvironment) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    dbScriptsMap
    |> Map.map Postgres.executeScripts
    |> Map.values
    |> Seq.map ((|>) schema >> ((|>) connectionString))
    |> ignore

    services
        .AddSingleton<NpgsqlDataSource>(fun sp ->
            sp.GetRequiredService<JsonSerializerOptions>()
            |> NpgsqlDataSourceBuilder(connectionString)
                .EnableParameterLogging(env.IsDevelopment())
                .ConfigureJsonOptions
            |> _.Build())
        .AddTransient<DbConnection>(_.GetRequiredService<NpgsqlDataSource>().CreateConnection())
        .AddSingleton<DbSchema>(schema)

let private configureSerialization (services: IServiceCollection) =
    JsonFSharpOptions.Default().ToJsonSerializerOptions() |> services.AddSingleton

let private configureGiraffe (services: IServiceCollection) =
    services
        .AddGiraffe()
        .AddSingleton<Json.ISerializer, Json.Serializer>(fun sp ->
            sp.GetRequiredService<JsonSerializerOptions>() |> Json.Serializer)

let private configureServices (config: IConfiguration) (env: IWebHostEnvironment) (services: IServiceCollection) =
    services.AddProblemDetails().AddGiraffe()
    |> configureSerialization
    |> configureDb config env
    |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    let config = builder.Configuration

    builder.AddServiceDefaults().AddDefaultAuthentication()
    |> configureServices config builder.Environment

    builder.AddRabbitMqEventBus("eventbus") |> ignore
    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffe webApp

    app
