[<RequireQualifiedAccess>]
module eShop.RabbitMQ.Configuration

open System

[<Literal>]
let internal ExchangeName = "eshop_event_bus"

[<Literal>]
let internal DeadLetterExchangeName = "eshop_event_bus_dlx"

[<Literal>]
let internal DeadLetterQueueName = "eshop_event_bus_dlq"

[<Literal>]
let internal DeadLetterExchangeArgName = "x-dead-letter-exchange"

[<Literal>]
let internal DeadLetterRoutingKeyArgName = "x-dead-letter-exchange"

[<Literal>]
let internal RetryCountArgName = "x-retry-count"

[<Literal>]
let internal RetryTimestampArgName = "x-retry-timestamp"

[<Literal>]
let SectionName = "EventBus"

[<CLIMutable>]
type RabbitMQOptions =
    { SubscriptionClientName: string
      RetryCount: int
      MessageTtl: TimeSpan }
