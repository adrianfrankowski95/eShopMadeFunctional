[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open RabbitMQ.Client
open FsToolkit.ErrorHandling
open RabbitMQ.Client.Events
open eShop.ConstrainedTypes

[<Literal>]
let private ExchangeName = "eshop_event_bus"

type QueueName = private QueueName of string

module QueueName =
    let create =
        [ String.Constraints.nonWhiteSpace
          String.Constraints.hasMaxUtf8Bytes 255
          String.Constraints.doesntStartWith "amqp." ]
        |> String.Constraints.evaluateM QueueName (nameof QueueName)

let configure (rabbitMqConnection: IConnection) (QueueName queueName) events =
    let rec ensureIsOpen (retries: TimeSpan list) =
        match rabbitMqConnection.IsOpen, retries with
        | true, _ -> true |> Async.retn
        | false, head :: tail ->
            async {
                do! Async.Sleep head
                return! ensureIsOpen tail
            }
        | false, [] -> false |> Async.retn

    asyncResult {
        do!
            [ (1: float); 2; 5 ]
            |> List.map TimeSpan.FromSeconds
            |> ensureIsOpen
            |> AsyncResult.requireTrue "Connection to RabbitMQ was not open"

        let channel = rabbitMqConnection.CreateModel()

        channel.ExchangeDeclare(ExchangeName, ``type`` = "direct")

        channel.QueueDeclare(queue = queueName, durable = true, exclusive = false, autoDelete = false)
        |> ignore

        let consumer = AsyncEventingBasicConsumer(channel)

        //channel.

        return failwith ""
    }
    |> AsyncResult.ignoreError
