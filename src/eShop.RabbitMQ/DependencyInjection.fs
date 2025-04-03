module eShop.RabbitMQ.DependencyInjection

open System
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

type IHostApplicationBuilder with
    member this.AddRabbitMQ(connectionName) =
        this.AddRabbitMQClient(
            connectionName,
            configureConnectionFactory =
                fun factory ->
                    factory.DispatchConsumersAsync <- true
                    factory.AutomaticRecoveryEnabled <- true
        )

        this.Services
            .Configure<Configuration.RabbitMQOptions>(this.Configuration.GetRequiredSection(Configuration.SectionName))
            .AddSingleton<AsyncEventingBasicConsumer>(fun sp ->
                let connection = sp.GetRequiredService<IConnection>()
                let config = sp.GetRequiredService<IOptions<Configuration.RabbitMQOptions>>().Value

                config
                |> RabbitMQ.initConsumer connection
                |> Async.RunSynchronously
                |> Result.valueOr failwith)
        |> ignore

        this

type IServiceCollection with
    // TODO: Add OpenTelemetry
    member this.RegisterRabbitMQEventHandler<'state, 'eventId, 'eventPayload, 'persistEventsIoError, 'publishEventsIoError>
        (
            eventNamesToHandle,
            aggregateIdSelector,
            deserializeEvent,
            getDependencies:
                IServiceProvider
                    -> PersistEvent<'state, 'eventId, 'eventPayload, 'persistEventsIoError> *
                    PublishEvents<'state, 'eventId, 'eventPayload, 'publishEventsIoError>
        ) =
        this.AddSingleton(
            typeof<IHostedService>,
            (fun sp ->
                { new IHostedService with
                    member this.StartAsync(cancellationToken) =
                        let logger =
                            sp.GetRequiredService<ILogger<RabbitMQEventDispatcher<'eventId, 'eventPayload>>>()

                        let getUtcNow = sp.GetRequiredService<GetUtcNow>()

                        let deserializeEvent =
                            sp.GetRequiredService<JsonSerializerOptions>() |> deserializeEvent

                        let consumer = sp.GetRequiredService<AsyncEventingBasicConsumer>()

                        let config = sp.GetRequiredService<IOptions<Configuration.RabbitMQOptions>>().Value

                        let persistEvents, publishEvents = sp |> getDependencies

                        RabbitMQ.registerEventHandler
                            eventNamesToHandle
                            aggregateIdSelector
                            deserializeEvent
                            consumer
                            config
                            logger
                            getUtcNow
                            persistEvents
                            publishEvents
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
