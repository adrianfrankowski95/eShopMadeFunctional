module eShop.RabbitMQ.Configuration

open System

[<Literal>]
let ExchangeName = "eshop_event_bus"

[<Literal>]
let DeadLetterExchangeName = "eshop_event_bus_dlx"

[<Literal>]
let DeadLetterQueueName = "eshop_event_bus_dlq"

[<Literal>]
let RetryCountArgName = "x-retry-count"

[<Literal>]
let RetryTimestampArgName = "x-retry-timestamp"

[<Literal>]
let SectionName = "EventBus"

[<CLIMutable>]
type RabbitMQOptions =
    { SubscriptionClientName: string
      RetryCount: int
      MessageTtl: TimeSpan }
