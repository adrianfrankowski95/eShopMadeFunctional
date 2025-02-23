[<RequireQualifiedAccess>]
module Ordering.API.Configuration

open System
open System.Data.Common
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Npgsql
open eShop.DomainDrivenDesign.Postgres
open eShop.DomainDrivenDesign.Postgres.DependencyInjection
open eShop.ServiceDefaults
open eShop.EventBusRabbitMQ
open Ordering.API.WebApp
open System.Text.Json.Serialization

let private configureSerialization (services: IServiceCollection) =
    JsonFSharpOptions.Default().ToJsonSerializerOptions() |> services.AddSingleton

let private configureDb (config: IConfiguration) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    let initEventProcessingDb = Postgres.createInitScriptForEventProcessing schema
    let initOrderingDb = Postgres.createScript "Ordering" "./dbinit" schema

    services
        .AddSingleton<NpgsqlDataSource>(
            Func<IServiceProvider, NpgsqlDataSource>(
                _.GetRequiredService<JsonSerializerOptions>()
                >> NpgsqlDataSourceBuilder(connectionString).ConfigureJsonOptions
                >> _.Build()
            )
        )
        .AddTransient<DbConnection>(_.GetRequiredService<NpgsqlDataSource>().CreateConnection())
    |> addDbSchema schema
    |> addDbScriptExecution initEventProcessingDb
    |> addDbScriptExecution initOrderingDb

let private configureGiraffe (services: IServiceCollection) =
    services
        .AddGiraffe()
        .AddSingleton<Json.ISerializer>(
            Func<IServiceProvider, Json.ISerializer>(fun sp ->
                let jsonOptions = sp.GetRequiredService<JsonSerializerOptions>()
                Json.FsharpFriendlySerializer(null, jsonOptions))
        )

let private configureServices (config: IConfiguration) (services: IServiceCollection) =
    services.AddProblemDetails().AddGiraffe()
    |> configureSerialization
    |> configureDb config
    |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    let config = builder.Configuration

    builder.AddServiceDefaults().AddDefaultAuthentication()
    |> configureServices config

    builder.AddRabbitMqEventBus("eventbus") |> ignore
    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffe webApp

    app
