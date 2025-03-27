[<RequireQualifiedAccess>]
module eShop.Ordering.API.Configuration

open System
open System.Data.Common
open System.IO
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open eShop.DomainDrivenDesign
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Adapters.Postgres
open eShop.Ordering.Domain.Model
open eShop.Postgres
open eShop.Postgres.DependencyInjection
open eShop.ServiceDefaults
open eShop.Prelude
open eShop.Ordering.API.WebApp
open System.Text.Json.Serialization
open FSharp.Data.LiteralProviders
open eShop.RabbitMQ.DependencyInjection
open FsToolkit.ErrorHandling
open eShop.Prelude.Operators

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

let private configurePostgres (config: IConfiguration) (env: IHostEnvironment) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    services.AddPostgres connectionString schema dbScripts env

let private configureSerialization (services: IServiceCollection) =
    JsonFSharpOptions.Default().ToJsonSerializerOptions() |> services.AddSingleton

let private configureTime (services: IServiceCollection) =
    services.AddTransient<GetUtcNow>(Func<IServiceProvider, GetUtcNow>(fun _ () -> DateTimeOffset.UtcNow))

let private configureIntegrationEventsProcessor (services: IServiceCollection) =
    services.AddSingleton<EventsProcessor<OrderAggregate.State,Postgres.EventId,IntegrationEvent.Consumed,SqlIoError,eShop.RabbitMQ.RabbitMQIoError>>(fun sp -> sp.GetRequiredService<IntegrationEventsProcessor>().Get)

let private configureRabbitMQ (services: IServiceCollection) =
    services.RegisterRabbitMQConsumer(
        IntegrationEvent.Consumed.names,
        IntegrationEvent.Consumed.getOrderId,
        IntegrationEvent.Consumed.deserialize,
        fun sp ->
            let dbConn = sp.GetRequiredService<DbConnection>()

            dbConn.Open()
            let transaction = dbConn.BeginTransaction()

            let persistEvents =
                sp
                    .GetRequiredService<IPostgresOrderIntegrationEventsProcessorAdapter>()
                    .PersistOrderIntegrationEvents(transaction)

            let processEvent =
                sp.GetRequiredService<IntegrationEventsProcessor>().Get.Process
                >>> AsyncResult.ok

            persistEvents, processEvent
    )

let private configureAdapters (services: IServiceCollection) =
    services
        .AddTransient<IPostgresOrderAggregateManagementAdapter, PostgresOrderAggregateManagementAdapter>()
        .AddTransient<IPostgresOrderAggregateEventsProcessorAdapter, PostgresOrderAggregateEventsProcessorAdapter>()
        .AddTransient<IPostgresOrderIntegrationEventsProcessorAdapter, PostgresOrderIntegrationEventsProcessorAdapter>()

let private configureGiraffe (services: IServiceCollection) =
    services
        .AddGiraffe()
        .AddSingleton<Json.ISerializer, Json.Serializer>(fun sp ->
            sp.GetRequiredService<JsonSerializerOptions>() |> Json.Serializer)

let private configureServices (builder: IHostApplicationBuilder) =
    builder.Services.AddProblemDetails().AddGiraffe()
    |> configureSerialization
    |> configureTime
    |> configurePostgres builder.Configuration builder.Environment
    |> configureRabbitMQ
    |> configureAdapters
    |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    builder.AddServiceDefaults().AddRabbitMQ("eventbus").AddDefaultAuthentication()
    |> configureServices

    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffe webApp

    app
