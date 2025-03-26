module eShop.RabbitMQ.DependencyInjection

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Logging
open RabbitMQ.Client
open eShop.DomainDrivenDesign

type IHostApplicationBuilder with
    member this.AddRabbitMQ =
        fun connectionName ->
            this.AddRabbitMQClient(
                connectionName,
                configureConnectionFactory =
                    fun factory ->
                        factory.DispatchConsumersAsync <- true
                        factory.AutomaticRecoveryEnabled <- true
            )

type IApplicationBuilder with
    member this.AddRabbitMQConsumer<'state, 'eventId, 'eventPayload>(eventsToConsume, selectAggregateId, deserializeEvent) =
        fun
            (getPersistEvents: IServiceProvider -> PersistEvents<'state, 'eventId, 'eventPayload, _>)
            (getPublishEvents: IServiceProvider -> PublishEvents<'state, 'eventId, 'eventPayload, _>) ->
            let connection = this.ApplicationServices.GetRequiredService<IConnection>()

            let logger =
                this.ApplicationServices.GetRequiredService<ILogger<RabbitMQEventDispatcher<'eventId, 'eventPayload>>>()

            let persistEvents = this.ApplicationServices |> getPersistEvents
            let publishEvents = this.ApplicationServices |> getPublishEvents

            let config =
                this.ApplicationServices
                    .GetRequiredService<IConfiguration>()
                    .GetRequiredSection(Configuration.SectionName)
                    .Get<Configuration.RabbitMqOptions>()

            use channel =
                eventsToConsume
                |> RabbitMQ.init connection config
                |> Async.RunSynchronously
                |> Result.valueOr failwith

            RabbitMQ.addConsumer logger config channel persistEvents publishEvents selectAggregateId deserializeEvent
