[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Common.IntegrationEvent

open System
open System.Text.Json
open Microsoft.FSharp.Reflection
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.RabbitMQ
open eShop.Prelude
open eShop.Ordering.Domain.Model

[<CLIMutable>]
type GracePeriodConfirmed = { OrderId: Guid }

[<CLIMutable>]
type OrderStockConfirmed = { OrderId: Guid }

[<CLIMutable>]
type ConfirmedOrderStockItem = { ProductId: int; HasStock: bool }

[<CLIMutable>]
type OrderStockRejected =
    { OrderId: Guid
      OrderStockItems: ConfirmedOrderStockItem list }

[<CLIMutable>]
type OrderPaymentFailed = { OrderId: Guid }

[<CLIMutable>]
type OrderPaymentSucceeded = { OrderId: Guid }

[<CLIMutable>]
type OrderStatusChangedToStarted =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToCancelled =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToShipped =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStockItem = { ProductId: int; Units: int }

[<CLIMutable>]
type OrderStatusChangedToAwaitingValidation =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string
      OrderStockItems: OrderStockItem list }

[<CLIMutable>]
type OrderStatusChangedToPaid =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string
      OrderStockItems: OrderStockItem list }

[<CLIMutable>]
type OrderStatusChangedToStockConfirmed =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToSubmitted =
    { OrderId: Guid
      BuyerName: string
      BuyerIdentityGuid: string }

// Note: It must stay in line with C# event names until we get rid of legacy code
let private createEventName value =
    value + "IntegrationEvent" |> RabbitMQ.EventName.create

let private forceEventName = createEventName >> Result.valueOr failwith

type Consumed =
    | GracePeriodConfirmed of GracePeriodConfirmed
    | OrderStockConfirmed of OrderStockConfirmed
    | OrderStockRejected of OrderStockRejected
    | OrderPaymentFailed of OrderPaymentFailed
    | OrderPaymentSucceeded of OrderPaymentSucceeded

[<RequireQualifiedAccess>]
module Consumed =
    let private nameTypeMap =
        FSharpType.GetUnionCases(typeof<Consumed>)
        |> Array.map (fun caseInfo ->
            let eventName = caseInfo.Name |> forceEventName
            let unionCaseType = caseInfo.GetFields() |> Array.head |> _.PropertyType

            eventName, (caseInfo, unionCaseType))
        |> Map.ofArray

    let eventNames = nameTypeMap |> Map.keys |> Set.ofSeq

    let deserialize (jsonOptions: JsonSerializerOptions) (eventName: RabbitMQ.EventName) (payload: string) =
        let deserialize targetType =
            JsonSerializer.Deserialize(payload, targetType, jsonOptions)

        let createUnion (targetUnionCase, data) =
            FSharpValue.MakeUnion(targetUnionCase, [| data |]) :?> Consumed

        fun () ->
            nameTypeMap
            |> Map.tryFind eventName
            |> Option.defaultWith (fun () -> failwith $"Unsupported event name: %s{eventName |> RabbitMQ.EventName.value}")
            |> Tuple.mapSnd deserialize |> createUnion
        |> Result.catch

    let getOrderAggregateId (integrationEvent: Consumed) =
        match integrationEvent with
        | GracePeriodConfirmed ev -> ev.OrderId
        | OrderStockConfirmed ev -> ev.OrderId
        | OrderStockRejected ev -> ev.OrderId
        | OrderPaymentFailed ev -> ev.OrderId
        | OrderPaymentSucceeded ev -> ev.OrderId
        |> AggregateId.ofGuid<Order.State>

type Published =
    | OrderStatusChangedToStarted of OrderStatusChangedToStarted
    | OrderStatusChangedToCancelled of OrderStatusChangedToCancelled
    | OrderStatusChangedToShipped of OrderStatusChangedToShipped
    | OrderStatusChangedToAwaitingValidation of OrderStatusChangedToAwaitingValidation
    | OrderStatusChangedToPaid of OrderStatusChangedToPaid
    | OrderStatusChangedToStockConfirmed of OrderStatusChangedToStockConfirmed
    | OrderStatusChangedToSubmitted of OrderStatusChangedToSubmitted

[<RequireQualifiedAccess>]
module Published =
    let ofAggregateEvent (AggregateId orderId) (domainEvent: Order.Event) =
        match domainEvent with
        | Order.Event.OrderStarted ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStarted)
            |> Published.OrderStatusChangedToStarted

        | Order.Event.PaymentMethodVerified ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToSubmitted)
            |> Published.OrderStatusChangedToSubmitted

        | Order.Event.OrderCancelled ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToCancelled)
            |> Published.OrderStatusChangedToCancelled

        | Order.Event.OrderStatusChangedToAwaitingValidation ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.StockToValidate
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, units) ->
                     { ProductId = productId |> ProductId.value
                       Units = units |> Units.value }) }
            : OrderStatusChangedToAwaitingValidation)
            |> Published.OrderStatusChangedToAwaitingValidation

        | Order.Event.OrderStockConfirmed ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStockConfirmed)
            |> Published.OrderStatusChangedToStockConfirmed

        | Order.Event.OrderPaid ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.PaidOrderItems
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, orderItem) ->
                     { ProductId = productId |> ProductId.value
                       Units = orderItem.Units |> Units.value }) }
            : OrderStatusChangedToPaid)
            |> Published.OrderStatusChangedToPaid

        | Order.Event.OrderShipped ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToShipped)
            |> Published.OrderStatusChangedToShipped

    let serialize (jsonOptions: JsonSerializerOptions) (integrationEvent: Published) =
        let inline serialize ev =
            JsonSerializer.SerializeToUtf8Bytes(ev, jsonOptions)

        fun () ->
            FSharpValue.GetUnionFields(integrationEvent, typeof<Published>)
            |> snd
            |> Array.head
            |> serialize
        |> Result.catch

    let getEventName (integrationEvent: Published) =
        FSharpValue.GetUnionFields(integrationEvent, typeof<Published>)
        |> fst
        |> _.Name
        |> createEventName
