[<RequireQualifiedAccess>]
module Ordering.API.Configuration

open System.Data.Common
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Data.SqlClient
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open eShop.DomainDrivenDesign.Postgres
open eShop.DomainDrivenDesign.Postgres.DependencyInjection
open eShop.ServiceDefaults
open eShop.EventBusRabbitMQ
open Ordering.API.WebApp

let private configureDb (config: IConfiguration) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    let initEventProcessingDb = Postgres.createInitScriptForEventProcessing schema
    let initOrderingDb = Postgres.createScript "Ordering" "./dbinit" schema

    services.AddTransient<DbConnection>(fun _ -> new SqlConnection(connectionString))
    |> addDbSchema schema
    |> addDbScript initEventProcessingDb
    |> addDbScript initOrderingDb

let private configureServices (config: IConfiguration) (services: IServiceCollection) =
    services.AddProblemDetails().AddGiraffe() |> configureDb config |> ignore

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
