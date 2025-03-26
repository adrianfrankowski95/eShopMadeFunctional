[<RequireQualifiedAccess>]
module eShop.Ordering.API.Configuration

open System.IO
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open eShop.Ordering.Adapters.RabbitMQ
open eShop.Postgres
open eShop.Postgres.DependencyInjection
open eShop.ServiceDefaults
open eShop.Prelude
open eShop.Ordering.API.WebApp
open System.Text.Json.Serialization
open FSharp.Data.LiteralProviders
open eShop.RabbitMQ.DependencyInjection
open eShop.RabbitMQ
open FsToolkit.ErrorHandling

let private dbScripts =
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
        TextFile<"../eShop.Ordering.Adapters.Postgres/dbinit/001_Create_Ordering_Tables.sql", EnsureExists=true>.Path
    |> Map.mapValues getRelativeDirectoryPath

let private configurePostgres (config: IConfiguration) (env: IWebHostEnvironment) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    services.AddPostgres connectionString schema dbScripts env

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
    |> configurePostgres config env
    |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    let config = builder.Configuration

    builder.AddServiceDefaults().AddDefaultAuthentication()
    |> configureServices config builder.Environment

    builder.AddRabbitMQ "eventbus"

    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffe webApp
    app.UseRabbitMQ
    
    app
