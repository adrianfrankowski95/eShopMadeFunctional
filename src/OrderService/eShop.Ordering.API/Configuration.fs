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
open eShop.DomainDrivenDesign
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
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

    services.AddPostgres(connectionString, schema, dbScripts, env)

let private configureSerialization (services: IServiceCollection) =
    JsonFSharpOptions.Default().ToJsonSerializerOptions() |> services.AddSingleton

let private configureTime (services: IServiceCollection) =
    services.AddTransient<GetUtcNow>(Func<IServiceProvider, GetUtcNow>(fun _ () -> DateTimeOffset.UtcNow))

let private configureGenerators (services: IServiceCollection) =
    services
        .AddTransient<GenerateEventId>(Func<IServiceProvider, GenerateEventId>(fun _ -> EventId.generate))
        .AddTransient<GenerateAggregateId<Order.State>>(
            Func<IServiceProvider, GenerateAggregateId<Order.State>>(fun _ -> AggregateId.generate)
        )

let private configureOrderAggregateEventsProcessor (services: IServiceCollection) =
    services.AddSingleton<CompositionRoot.OrderAggregateEventsProcessor>(
        CompositionRoot.OrderAggregateEventsProcessor.buildFromSp
    )

let private configureOrderIntegrationEventsProcessor (services: IServiceCollection) =
    services.AddSingleton<CompositionRoot.OrderIntegrationEventsProcessor>(
        CompositionRoot.OrderIntegrationEventsProcessor.buildFromSp
    )

let private configureRabbitMQ (services: IServiceCollection) =
    services.AddRabbitMQEventHandler(
        IntegrationEvent.Consumed.eventNames,
        IntegrationEvent.Consumed.getOrderAggregateId,
        IntegrationEvent.Consumed.deserialize,
        CompositionRoot.buildPersistIntegrationEventsFromSp
    )

let private configureAdapters (services: IServiceCollection) =
    services
        .AddTransient<ISqlOrderAggregateManagementAdapter, PostgresOrderAggregateManagementAdapter>()
        .AddTransient<ISqlOrderIntegrationEventsAdapter, PostgresOrderIntegrationEventsAdapter>()
        .AddTransient<ISqlPaymentManagementAdapter, PostgresPaymentManagementAdapter>()
        .AddTransient<IHttpPaymentManagementAdapter, HttpPaymentManagementAdapter>(
            _.GetRequiredService<IConfiguration>().GetValue<bool>("ShouldAcceptPayment")
            >> HttpPaymentManagementAdapter
        )

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
