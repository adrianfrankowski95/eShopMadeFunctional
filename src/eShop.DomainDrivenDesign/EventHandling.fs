namespace eShop.DomainDrivenDesign

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open eShop.Prelude
open FsToolkit.ErrorHandling

type Event<'payload> =
    { Data: 'payload; OccurredAt: DateTimeOffset }


type EventHandler<'eventPayload, 'ioError> = Event<'eventPayload> -> AsyncResult<unit, 'ioError>


type HandlerName = string
type AggregateTypeName = string


type EventHandlerRegistry<'eventPayload, 'ioError> = private EventHandlerRegistry of Map<AggregateTypeName * HandlerName, EventHandler<'eventPayload, 'ioError>>

module EventHandlerRegistry =
    let empty<'eventPayload, 'ioError>: EventHandlerRegistry<'eventPayload, 'ioError> =
        Map.empty |> EventHandlerRegistry
        
    let register<'state, 'eventPayload, 'ioError> handlerName (handler: EventHandler<'eventPayload, 'ioError>) (EventHandlerRegistry registry)=
        let aggregateName = typedefof<'state>.Name
        
        registry |> Map.add (aggregateName, handlerName) handler |> EventHandlerRegistry


[<RequireQualifiedAccess>]
module EventsProcessor =
    type private Delay = TimeSpan
    type private Attempt = int
    
    type EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        private
            { EventHandlerRegistry: EventHandlerRegistry<'eventPayload, 'ioError>; Retries: Delay list }
            
    let init<'state, 'eventPayload, 'ioError>: EventsProcessorOptions<'state, 'eventPayload, 'ioError> =
        { EventHandlerRegistry = EventHandlerRegistry.empty
          Retries =
              [ TimeSpan.FromMinutes(1: float)
                TimeSpan.FromMinutes(2: float)
                TimeSpan.FromMinutes(3: float)
                TimeSpan.FromMinutes(20: float) ] }
        
    let withRetries retries options = { options with Retries = retries }
    
    let registerHandler<'state, 'eventPayload, 'ioError>
        handlerName
        (handler: EventHandler<'eventPayload, 'ioError>)
        (options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>)
        =
            { options with EventHandlerRegistry = options.EventHandlerRegistry |> EventHandlerRegistry.register<'state, 'eventPayload, 'ioError> handlerName handler }
            
    type private Command<'eventPayload, 'ioError> =
        | HandleEvents of Event<'eventPayload> list
        | TriggerRetry of Attempt * (Event<'eventPayload> * EventHandler<'eventPayload, 'ioError> list)
        
    type T<'state, 'eventPayload, 'ioError> internal (logger: ILogger, options: EventsProcessorOptions<'state, 'eventPayload, 'ioError>) =
        let scheduleRetry reply attempt eventAndFailedHandlers =
            task {
                do!
                    options.Retries
                    |> List.item (attempt - 1)
                    |> Task.Delay
                    
                return (attempt, eventAndFailedHandlers) |> TriggerRetry |> reply
            }
            
        let handleEventsAndCollectFailedHandlers (handlers: EventHandler<'eventPayload, 'ioError> list) attempt event =
            async {
                let! failedHandlers =
                    handlers
                    |> List.traverseAsyncResultA (fun handler ->
                        event
                        |> handler
                        |> 
                        )
            }