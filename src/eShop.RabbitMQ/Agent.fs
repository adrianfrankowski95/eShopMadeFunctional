[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open FsToolkit.ErrorHandling

type private Message =
    | Publish of EventName * Event<obj> * EventPriority
    | Consume of EventName * Event<byte array>

module Message =
    let getWithHighPriority msg=
        match msg with
        | Publish (_, _, priority) -> msg |> Some
        | Consume _ -> None

type Agent internal (eventBus: EventBus, eventHandlers: EventHandlers) =
    let agent = MailboxProcessor<Message>.Start((fun inbox ->
        let rec loop (publisher: Publisher, consumer: Consumer) =
            async {
              let! maybeHighPriority = inbox.TryScan (Message.getWithHighPriority >> Option.map Async.singleton)
              let! msg = maybeHighPriority |> Option.map Async.singleton |> Option.defaultWith inbox.Receive
            
              match msg with
              | Publish (eventName, ev, _) ->
              | Consume (eventName, ev) ->
            }
        
        async {
            let! publisher = eventBus |> EventBus.createPublisher |> Async.AwaitTask
            let! consumer = eventBus |> EventBus.createConsumer |> Async.AwaitTask
            
            return ()
        }))
    
    member _.Publish<'payload> (ev: Event<'payload>) (priority: EventPriority) =
        let eventName = ev |> Publisher.getEventName
        let boxedEvent = ev |> Event.mapPayload box

        (eventName, boxedEvent, priority) |> Publish |> agent.Post

    interface IDisposable with
        member _.Dispose() = agent.Dispose()