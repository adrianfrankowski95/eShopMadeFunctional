[<RequireQualifiedAccess>]
module eShop.RabbitMQ.Configuration

open System.Diagnostics
open OpenTelemetry.Context.Propagation

[<Literal>]
let ExchangeName = "eshop_event_bus"

[<Literal>]
let SectionName = "EventBus"

module DeadLetter =
    [<Literal>]
    let ExchangeName = "eshop_event_bus_dlx"

    [<Literal>]
    let QueueName = "eshop_event_bus_dlq"

    [<Literal>]
    let RoutingKey = "dead_letter"

type OpenTelemetry =
    { ActivitySource: ActivitySource
      Propagator: TextMapPropagator }

module OpenTelemetry =
    [<Literal>]
    let ActivitySourceName = "EventBusRabbitMQ"

    let init =
        { ActivitySource = new ActivitySource(ActivitySourceName)
          Propagator = Propagators.DefaultTextMapPropagator }

[<CLIMutable>]
type RabbitMqOptions =
    { SubscriptionClientName: string
      RetryCount: int }
