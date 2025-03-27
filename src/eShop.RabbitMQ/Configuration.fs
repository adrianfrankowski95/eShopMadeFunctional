[<RequireQualifiedAccess>]
module eShop.RabbitMQ.Configuration

[<Literal>]
let ExchangeName = "eshop_event_bus"

[<Literal>]
let SectionName = "EventBus"

[<Literal>]
let DeadLetterExchangeName = "eshop_event_bus_dlx"

[<Literal>]
let DeadLetterQueueName = "eshop_event_bus_dlq"

[<CLIMutable>]
type RabbitMQOptions =
    { SubscriptionClientName: string
      RetryCount: int }
