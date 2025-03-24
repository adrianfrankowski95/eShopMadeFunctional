[<RequireQualifiedAccess>]
module internal eShop.RabbitMQ.Consumer

open RabbitMQ.Client
open RabbitMQ.Client.Events
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System
open eShop.DomainDrivenDesign

let private deserializeEvent<'T> (body: ReadOnlyMemory<byte>) (eventName: string) =
    try
        let json = Encoding.UTF8.GetString(body.Span)
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let message = JsonSerializer.Deserialize<Event<'T>>(json, options)

        if typeof<'T>.Name = eventName then
            Ok message
        else
            Error $"Event name mismatch. Expected: %s{eventName}, Received: %s{typeof<'T>.Name}"
    with ex ->
        Error $"Failed to deserialize event: %s{ex.Message}"

// let setupConsumer<'T>
//     (channel: IModel)
//     (queueName: string)
//     (eventName: string)
//     (handler: EventHandler<'T>)
//     (prefetchCount: uint16)
//     =
//     // Set prefetch count to control how many messages can be consumed at once
//     channel.BasicQos(0u, prefetchCount, false)
//
//     let consumer = EventingBasicConsumer(channel)
//
//     consumer.Received.Add(fun ea ->
//         task {
//             let messageId = ea.BasicProperties.MessageId
//             
//             let! result =
//
//                 task {
//                     try
//                         match deserializeEvent<'T> ea.Body eventName with
//                         | Ok event ->
//                             let! handlerResult = handler event
//                             return handlerResult
//                         | Error e -> return Error e
//                     with ex ->
//                         return Error $"Exception in consumer: %s{ex.Message}"
//                 }
//
//             match result with
//             | Ok _ -> channel.BasicAck(ea.DeliveryTag, multiple = false)
//             | Error e ->
//                 printfn $"Error processing message %s{messageId}: %s{e}"
//                 channel.BasicNack(ea.DeliveryTag, multiple = false, requeue = false)
//         }
//         |> ignore)
//
//     channel.BasicConsume(queueName, false, consumer) |> ignore
