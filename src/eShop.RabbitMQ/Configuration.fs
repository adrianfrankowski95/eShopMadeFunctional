module eShop.RabbitMQ.Configuration

open System

[<Literal>]
let SectionName = "EventBus"

[<CLIMutable>]
type RabbitMQOptions =
    { SubscriptionClientName: string
      RetryCount: int
      MessageTtl: TimeSpan }
