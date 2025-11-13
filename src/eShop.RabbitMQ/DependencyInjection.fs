module eShop.RabbitMQ.DependencyInjection

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open RabbitMQ.Client
open RabbitMQ.Client.Events
open eShop.DomainDrivenDesign
open eShop.Prelude

type Extensions =
    [<Extension>]
    static member AddRabbitMQ(builder: IHostApplicationBuilder, connectionName) =
        builder.AddRabbitMQClient(
            connectionName,
            configureSettings =
                (fun settings ->
                    settings.DisableTracing <- false
                    settings.DisableHealthChecks <- false),
            configureConnectionFactory =
                (fun factory ->
                    factory.DispatchConsumersAsync <- true
                    factory.AutomaticRecoveryEnabled <- true)
        )

        builder.Services
            .Configure<Configuration.EventBusOptions>(
                builder.Configuration.GetRequiredSection(Configuration.SectionName)
            )
            .AddSingleton<AsyncEventingBasicConsumer>(fun sp ->
                let connection = sp.GetRequiredService<IConnection>()
                let config = sp.GetRequiredService<IOptions<Configuration.EventBusOptions>>().Value

                config
                |> RabbitMQ.initConsumer connection
                |> Task.getResultSynchronously
                |> Result.valueOr failwith)
        |> ignore

        builder

    [<Extension>]
    static member AddRabbitMQEventHandler<'state, 'eventPayload, 'eventLogIoError, 'eventHandlingIoError>
        (
            services: IServiceCollection,
            eventNamesToHandle,
            aggregateIdSelector,
            deserializeEvent,
            buildPersistEvents: IServiceProvider -> PersistEvents<'state, 'eventPayload, 'eventLogIoError>
        ) =
        services.AddSingleton(
            typeof<IHostedService>,
            (fun sp ->
                { new IHostedService with
                    member this.StartAsync(cancellationToken) =
                        let logger = sp.GetRequiredService<ILogger<'eventPayload>>()

                        let getUtcNow = sp.GetRequiredService<GetUtcNow>()

                        let deserializeEvent =
                            sp.GetRequiredService<JsonSerializerOptions>() |> deserializeEvent

                        let consumer = sp.GetRequiredService<AsyncEventingBasicConsumer>()

                        let config = sp.GetRequiredService<IOptions<Configuration.EventBusOptions>>().Value

                        let persistEvents = sp |> buildPersistEvents

                        RabbitMQ.registerEventHandler
                            eventNamesToHandle
                            aggregateIdSelector
                            deserializeEvent
                            consumer
                            config
                            logger
                            getUtcNow
                            persistEvents
                        |> Result.valueOr failwith

                        Task.CompletedTask

                    member this.StopAsync(cancellationToken) =
                        try
                            sp.GetRequiredService<AsyncEventingBasicConsumer>().Model.Dispose()
                        with _ ->
                            ()

                        Task.CompletedTask }
                :> obj)
        )
