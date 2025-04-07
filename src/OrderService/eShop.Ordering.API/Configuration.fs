[<RequireQualifiedAccess>]
module eShop.Ordering.API.Configuration

open System
open System.IO
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters.Common
open eShop.Postgres
open eShop.Postgres.DependencyInjection
open eShop.ServiceDefaults
open eShop.Prelude
open eShop.Ordering.API.WebApp
open System.Text.Json.Serialization
open FSharp.Data.LiteralProviders
open eShop.RabbitMQ.DependencyInjection

let private dbScripts =
    let getRelativeDirectoryPath path =
        Path.GetRelativePath(__SOURCE_DIRECTORY__, path) |> Path.GetDirectoryName

    let eventProcessingInitScriptPath =
        TextFile<
            "../../eShop.DomainDrivenDesign.Postgres/dbinit/001_Create_EventProcessingLog_Table.sql",
            EnsureExists=true
         >.Path

    let orderingInitScriptPath =
        TextFile<"../eShop.Ordering.Adapters.Postgres/dbinit/001_Create_Ordering_Tables.sql", EnsureExists=true>.Path

    Map.empty
    |> Map.add "EventProcessing" eventProcessingInitScriptPath
    |> Map.add "Ordering" orderingInitScriptPath
    |> Map.mapValues getRelativeDirectoryPath

let private configurePostgres (config: IConfiguration) (env: IHostEnvironment) (services: IServiceCollection) =
    let schema = "ordering" |> DbSchema
    let connectionString = config.GetConnectionString("orderingdb")

    services.AddPostgres connectionString schema dbScripts env

let private configureSerialization (services: IServiceCollection) =
    JsonFSharpOptions.Default().ToJsonSerializerOptions() |> services.AddSingleton

let private configureTime (services: IServiceCollection) =
    services.AddTransient<GetUtcNow>(Func<IServiceProvider, GetUtcNow>(fun _ () -> DateTimeOffset.UtcNow))

let private configureGenerators (services: IServiceCollection) =
    services.AddTransient<GenerateId<eventId>>(Func<IServiceProvider, GenerateId<eventId>>(fun _ -> EventId.generate))

let private configureOrderAggregateEventsProcessor (services: IServiceCollection) =
    services.AddSingleton<CompositionRoot.OrderAggregateEventsProcessor>(
        CompositionRoot.buildOrderAggregateEventsProcessorFromSp
    )

let private configureOrderIntegrationEventsProcessor (services: IServiceCollection) =
    services.AddSingleton<CompositionRoot.OrderIntegrationEventsProcessor>(
        CompositionRoot.buildOrderIntegrationEventsProcessorFromSp
    )

let private configureRabbitMQ (services: IServiceCollection) =
    services.AddRabbitMQEventHandler<_, _, SqlIoError>(
        IntegrationEvent.Consumed.eventNames,
        IntegrationEvent.Consumed.getOrderId,
        IntegrationEvent.Consumed.deserialize
    )

let private configureAdapters (services: IServiceCollection) =
    services
        .AddTransient<ISqlOrderAggregateManagementPort, PostgresOrderAggregateManagementAdapter>()
        .AddTransient<ISqlOrderAggregateEventsProcessorPort, PostgresOrderAggregateEventsProcessorAdapter>()
        .AddTransient<ISqlOrderIntegrationEventsProcessorPort, PostgresOrderIntegrationEventsProcessorAdapter>()
        .AddTransient<ISqlPaymentManagementPort, PostgresPaymentManagementAdapter>()

let private configureGiraffe (services: IServiceCollection) =
    services
        .AddGiraffe()
        .AddSingleton<Json.ISerializer, Json.Serializer>(fun sp ->
            sp.GetRequiredService<JsonSerializerOptions>() |> Json.Serializer)

let private configureServices (builder: IHostApplicationBuilder) =
    builder.Services.AddProblemDetails().AddGiraffe()
    |> configureSerialization
    |> configureTime
    |> configureGenerators
    |> configurePostgres builder.Configuration builder.Environment
    |> configureRabbitMQ
    |> configureAdapters
    |> configureOrderAggregateEventsProcessor
    |> configureOrderIntegrationEventsProcessor
    |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    builder.AddServiceDefaults().AddRabbitMQ("eventbus").AddDefaultAuthentication()
    |> configureServices

    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffeErrorHandler(GiraffeExtensions.errorHandler).UseGiraffe webApp

    app
