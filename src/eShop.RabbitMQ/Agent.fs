[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open System.Collections.Immutable
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open eShop.Prelude


type private Message<'payload> =
    | Publish of Event<'payload> * EventPriority
    | Consume of EventName * Event<byte array>

type Agent internal (eventBus: EventBus) =

    let processor = MailboxProcessor<Message<_>>.Start((fun inbox ->
        let rec loop (state: State) =
            async {
                let! cancellationRequested = Async.CancellationToken |> Async.map _.IsCancellationRequested
                
                if cancellationRequested then
                    state |> dispose
                    return ()
                
                let! maybeHighPriority = inbox.TryScan(Message.getHighPriority >> Option.map Async.singleton)

                let! msg =
                    maybeHighPriority
                    |> Option.map Async.singleton
                    |> Option.defaultWith inbox.Receive

                return!
                    match msg with
                    | Publish(eventName, event, _) ->
                        let exchangeName = Configuration.MainExchangeName

                        let body =
                            JsonSerializer.SerializeToUtf8Bytes(event.Data, jsonOptions) |> ReadOnlyMemory

                        let channel =
                            state.Channel
                            |> Option.defaultWith (fun () ->
                                let connection = state.GetOrCreateConnection()
                                let channel = connection.CreateModel()
                                channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")
                                channel)

                        let properties = channel.CreateBasicProperties()
                        properties.MessageId <- event.Id |> EventId.toString
                        properties.Type <- eventName
                        properties.DeliveryMode <- 2uy
                        properties.Timestamp <- event.OccurredAt.ToUnixTimeSeconds() |> AmqpTimestamp
                        properties.ContentType <- "application/json"
                        properties.Persistent <- true
                        properties.Expiration <- options.MessageTtl.TotalMilliseconds |> int |> string

                        channel.BasicPublish(
                            exchange = exchangeName,
                            routingKey = eventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )

                        state |> State.withChannel channel

                    | Retry(ea, retryCount) ->
                        let retryCountArgName = Configuration.RetryCountArgName
                        let retryTimestampArgName = Configuration.RetryTimestampArgName

                        let channel =
                            state.Channel
                            |> Option.defaultWith (fun () ->
                                let connection = state.GetOrCreateConnection()
                                let channel = connection.CreateModel()
                                channel.ExchangeDeclare(exchange = ea.Exchange, ``type`` = "direct")
                                channel)

                        let newRetryCount = retryCount + 1
                        let retryTimestamp = getUtcNow () |> _.ToUnixTimeSeconds() |> AmqpTimestamp

                        ea.BasicProperties.Headers.Add(retryCountArgName, newRetryCount)
                        ea.BasicProperties.Headers.Add(retryTimestampArgName, retryTimestamp)

                        channel.BasicPublish(
                            exchange = ea.Exchange,
                            routingKey = ea.RoutingKey,
                            mandatory = true,
                            basicProperties = ea.BasicProperties,
                            body = ea.Body
                        )

                        state |> State.withChannel channel
                    |> loop
            }
        
        inbox |> registerHandlers
        
        State.init connectionFactory
        |> State.withConsumer consumer
        |> State.withConsumerChannel consumerChannel
        |> loop), cts.Token)

    //member _.Start(initState: State)
    
    member _.Publish<'payload> (ev: Event<'payload>) (priority: EventPriority) =
        let eventName = ev |> getEventName
        let boxedEvent = ev |> Event.mapPayload box

        (eventName, boxedEvent, priority) |> Message.Publish |> processor.Post

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel(false)
            cts.Dispose()
            processor.Dispose()