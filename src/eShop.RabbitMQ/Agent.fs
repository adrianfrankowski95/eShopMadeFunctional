[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open eShop.RabbitMQ
open eShop.DomainDrivenDesign

type private Message =
    | Publish of EventName * Event<obj> * EventPriority
    | Consume of EventName * Event<byte array>

type Agent internal (eventBus: EventBus, eventHandlers: EventHandlers) =
    let agent = MailboxProcessor<Message>.Start((fun inbox ->
        let rec loop (publisher: Publisher, consumer: Consumer) =
            async {
              
            }
        
        async {
            
        }))
    
    member _.Publish<'payload> (ev: Event<'payload>) (priority: EventPriority) =
        let eventName = ev |> Publisher.getEventName
        let boxedEvent = ev |> Event.mapPayload box

        (eventName, boxedEvent, priority) |> Publish |> agent.Post

    interface IDisposable with
        member _.Dispose() =
            agent.Dispose()