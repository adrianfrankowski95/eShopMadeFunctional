namespace eShop.RabbitMQ

open System

type EventMessage<'T> =
    { Id: Guid
      CorrelationId: Guid option
      CreatedAt: DateTimeOffset
      EventName: string
      Payload: 'T
      Metadata: Map<string, string> }

type ConsumerConfig =
    { QueueName: string
      ExchangeName: string
      DeadLetterExchange: string option
      RetryCount: int
      PrefetchCount: uint16 }

type PublisherConfig =
    { ExchangeName: string
      ExchangeType: string
      Durable: bool
      AutoDelete: bool }

type ConnectionConfig =
    { HostName: string
      Port: int
      UserName: string
      Password: string
      VirtualHost: string
      SslEnabled: bool }

type RabbitMQConfig =
    { Connection: ConnectionConfig
      Consumer: ConsumerConfig
      Publisher: PublisherConfig }
