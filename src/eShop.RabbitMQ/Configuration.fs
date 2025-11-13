module eShop.RabbitMQ.Configuration

open System

[<Literal>]
let MainExchangeName = "eshop_event_bus"

[<Literal>]
let MainDeadLetterExchangeName = "eshop_event_bus_dlx"

[<Literal>]
let MainDeadLetterQueueName = "eshop_event_bus_dlq"

[<Literal>]
let RetryCountArgName = "x-retry-count"

[<Literal>]
let RetryTimestampArgName = "x-retry-timestamp"

[<Literal>]
let SectionName = "EventBus"

[<CLIMutable>]
type EventBusOptions =
    { SubscriptionClientName: string
      MessageTtl: TimeSpan
      Retries: TimeSpan[] }
