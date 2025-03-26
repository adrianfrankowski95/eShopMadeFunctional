module eShop.RabbitMQ.DependencyInjection

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open FsToolkit.ErrorHandling

type IHostApplicationBuilder with
    member this.AddRabbitMQ =
        fun connectionName eventsToConfigure ->
            let config =
                this.Configuration
                    .GetRequiredSection(Configuration.SectionName)
                    .Get<Configuration.RabbitMqOptions>()

            this.AddRabbitMQClient(
                connectionName,
                configureConnectionFactory =
                    fun factory ->
                        factory.DispatchConsumersAsync <- true
                        factory.AutomaticRecoveryEnabled <- true

                        eventsToConfigure
                        |> RabbitMQ.init factory config
                        |> Async.RunSynchronously
                        |> Result.valueOr failwith
            )

            this
