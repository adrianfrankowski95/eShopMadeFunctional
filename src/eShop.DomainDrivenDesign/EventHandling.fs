namespace eShop.DomainDrivenDesign

open System
open eShop.Prelude

type Event<'payload> =
    { Data: 'payload; OccurredAt: DateTimeOffset }


type EventHandler<'eventPayload, 'ioError> = Event<'eventPayload> -> AsyncResult<unit, 'ioError>


type HandlerName = string

type AggregateTypeName = string


type EventHandlerRegistry<'eventPayload, 'ioError> = private EventHandlerRegistry of Map<AggregateTypeName * HandlerName, EventHandler<'eventPayload, 'ioError>>

module EventHandlerRegistry =
    let empty<'eventPayload, 'ioError>: EventHandlerRegistry<'eventPayload, 'ioError> =
        Map.empty |> EventHandlerRegistry
        
    let register<'state, 'eventPayload, 'ioError> handlerName (handler: EventHandler<'eventPayload, 'ioError>) registry=
        let aggregateName = typedefof<'state>.Name
        
        registry |> Map.add (aggregateName, handlerName) handler