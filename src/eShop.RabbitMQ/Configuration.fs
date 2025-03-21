module eShop.RabbitMQ.Configuration

open Microsoft.Extensions.Configuration

let private getConnectionConfig (configuration: IConfiguration) =
    { HostName = configuration["RabbitMQ:Connection:HostName"]
      Port = int (configuration["RabbitMQ:Connection:Port"])
      UserName = configuration["RabbitMQ:Connection:UserName"]
      Password = configuration["RabbitMQ:Connection:Password"]
      VirtualHost = configuration["RabbitMQ:Connection:VirtualHost"]
      SslEnabled = bool.Parse(configuration["RabbitMQ:Connection:SslEnabled"]) }

let private getConsumerConfig (configuration: IConfiguration) (serviceName: string) =
    { QueueName = serviceName
      ExchangeName = configuration["RabbitMQ:Consumer:ExchangeName"]
      DeadLetterExchange =
        match configuration["RabbitMQ:Consumer:DeadLetterExchange"] with
        | null -> None
        | value -> Some value
      RetryCount = int configuration["RabbitMQ:Consumer:RetryCount"]
      PrefetchCount = uint16 (int (configuration["RabbitMQ:Consumer:PrefetchCount"])) }

let private getPublisherConfig (configuration: IConfiguration) =
    { ExchangeName = configuration["RabbitMQ:Publisher:ExchangeName"]
      // Default to "direct" if not specified in configuration
      ExchangeType =
        match configuration["RabbitMQ:Publisher:ExchangeType"] with
        | null -> "direct"
        | value -> value
      Durable = bool.Parse(configuration["RabbitMQ:Publisher:Durable"])
      AutoDelete = bool.Parse(configuration["RabbitMQ:Publisher:AutoDelete"]) }

let getConfig (configuration: IConfiguration) (serviceName: string) =
    { Connection = getConnectionConfig configuration
      Consumer = getConsumerConfig configuration serviceName
      Publisher = getPublisherConfig configuration }
