module eShop.RabbitMQ.Configuration

open System

[<Literal>]
let internal MainExchangeName = "eshop_event_bus"

[<Literal>]
let internal MainDeadLetterExchangeName = "eshop_event_bus_dlx"

[<Literal>]
let internal MainDeadLetterQueueName = "eshop_event_bus_dlq"

[<Literal>]
let internal RetryCountArgName = "x-retry-count"

[<Literal>]
let internal UnroutableArgName = "x-unroutable"

[<Literal>]
let internal OriginalExchangeArgName = "x-original-exchange"

[<Literal>]
let SectionName = "EventBus"

[<CLIMutable>]
type EventBusOptions =
    { SubscriptionClientName: string
      MessageTtl: TimeSpan
      Retries: TimeSpan[] }
