[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open System.Text.Json
open Microsoft.Extensions.Logging
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open eShop.Prelude.Operators
open FsToolkit.ErrorHandling

type private Message =
    | Publish of EventName * Event<obj> * EventPriority * AsyncReplyChannel<Result<unit, PublishError>>
    | Consume of EventName * Event<byte array> * AsyncReplyChannel<Result<unit, EventHandlingError<obj>>>

module private Message =
    let getWithHighPriority msg =
        match msg with
        | Publish(_, _, priority, _) when priority = EventPriority.High -> msg |> Some
        | Publish _ -> None
        | Consume _ -> None

type Agent
    internal
    (eventBus: EventBus, eventHandlers: EventHandlers, jsonOptions: JsonSerializerOptions, logger: ILogger<RabbitMQ>) =
    let publishEvent = Publisher.internalPublish jsonOptions >>>> Async.AwaitTask

    let handleEvent =
        eventHandlers |> EventHandlers.invoke jsonOptions >>> Async.AwaitTask

    let subscribe = Consumer.subscribe logger

    let agent =
        MailboxProcessor<Message>.Start(fun inbox ->
            let reply (channel: AsyncReplyChannel<_>) x = x |> Async.map channel.Reply

            let rec loop (publisher: Publisher) =
                async {
                    let! maybeHighPriority = inbox.TryScan(Message.getWithHighPriority >> Option.map Async.singleton)

                    let! msg =
                        maybeHighPriority
                        |> Option.map Async.singleton
                        |> Option.defaultWith inbox.Receive

                    match msg with
                    | Publish(evName, ev, _, r) -> do! publisher |> publishEvent evName ev |> reply r

                    | Consume(evName, ev, r) -> do! ev |> handleEvent evName |> reply r

                    return! publisher |> loop
                }

            async {
                let! publisher = eventBus |> EventBus.createPublisher |> Async.AwaitTask
                let! consumer = eventBus |> EventBus.createConsumer |> Async.AwaitTask

                consumer
                |> subscribe (fun evName ev ->
                    inbox.PostAndAsyncReply(fun r -> (evName, ev, r) |> Consume)
                    |> Async.StartImmediateAsTask)

                do! publisher |> loop
            })

    member _.Publish<'payload> (ev: Event<'payload>) (priority: EventPriority) =
        let eventName = ev |> Publisher.getEventName
        let boxedEvent = ev |> Event.mapPayload box

        agent.PostAndAsyncReply(fun reply -> (eventName, boxedEvent, priority, reply) |> Publish)
        |> Async.StartImmediateAsTask

    interface IDisposable with
        member _.Dispose() = agent.Dispose()
