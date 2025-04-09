[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Common.IntegrationEvent

open System.Text.Json
open Microsoft.FSharp.Reflection
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.RabbitMQ
open eShop.Prelude
open eShop.Ordering.Domain.Model

[<CLIMutable>]
type GracePeriodConfirmed = { OrderId: int }

[<CLIMutable>]
type OrderStockConfirmed = { OrderId: int }

[<CLIMutable>]
type ConfirmedOrderStockItem = { ProductId: int; HasStock: bool }

[<CLIMutable>]
type OrderStockRejected =
    { OrderId: int
      OrderStockItems: ConfirmedOrderStockItem list }

[<CLIMutable>]
type OrderPaymentFailed = { OrderId: int }

[<CLIMutable>]
type OrderPaymentSucceeded = { OrderId: int }

[<CLIMutable>]
type OrderStatusChangedToStarted =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToCancelled =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToShipped =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStockItem = { ProductId: string; Units: int }

[<CLIMutable>]
type OrderStatusChangedToAwaitingValidation =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string
      OrderStockItems: OrderStockItem list }

[<CLIMutable>]
type OrderStatusChangedToPaid =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string
      OrderStockItems: OrderStockItem list }

[<CLIMutable>]
type OrderStatusChangedToStockConfirmed =
    { OrderId: int
      BuyerName: string
      BuyerIdentityGuid: string }

[<CLIMutable>]
type OrderStatusChangedToSubmitted =
    { OrderId: int
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

    let deserialize (jsonOptions: JsonSerializerOptions) (eventName: RabbitMQ.EventName) (json: string) =
        let deserialize targetType =
            JsonSerializer.Deserialize(json, targetType, jsonOptions)

        let createUnion (targetUnionCase, data) =
            FSharpValue.MakeUnion(targetUnionCase, [| data |]) :?> Consumed

        fun () -> nameTypeMap |> Map.find eventName |> Tuple.mapSnd deserialize |> createUnion
        |> Result.catch

    let getOrderAggregateId (integrationEvent: Consumed) =
        match integrationEvent with
        | GracePeriodConfirmed ev -> ev.OrderId
        | OrderStockConfirmed ev -> ev.OrderId
        | OrderStockRejected ev -> ev.OrderId
        | OrderPaymentFailed ev -> ev.OrderId
        | OrderPaymentSucceeded ev -> ev.OrderId
        |> AggregateId.ofInt<OrderAggregate.State>

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
    let ofAggregateEvent (AggregateId orderId) (domainEvent: OrderAggregate.Event) =
        match domainEvent with
        | OrderAggregate.Event.OrderStarted ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStarted)
            |> Published.OrderStatusChangedToStarted

        | OrderAggregate.Event.PaymentMethodVerified ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToSubmitted)
            |> Published.OrderStatusChangedToSubmitted

        | OrderAggregate.Event.OrderCancelled ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToCancelled)
            |> Published.OrderStatusChangedToCancelled

        | OrderAggregate.Event.OrderStatusChangedToAwaitingValidation ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.StockToValidate
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, units) ->
                     { ProductId = productId |> ProductId.toString
                       Units = units |> Units.value }) }
            : OrderStatusChangedToAwaitingValidation)
            |> Published.OrderStatusChangedToAwaitingValidation

        | OrderAggregate.Event.OrderStockConfirmed ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStockConfirmed)
            |> Published.OrderStatusChangedToStockConfirmed

        | OrderAggregate.Event.OrderPaid ev ->
            ({ OrderId = orderId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.PaidOrderItems
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, orderItem) ->
                     { ProductId = productId |> ProductId.toString
                       Units = orderItem.Units |> Units.value }) }
            : OrderStatusChangedToPaid)
            |> Published.OrderStatusChangedToPaid

        | OrderAggregate.Event.OrderShipped ev ->
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

    let createEventName (integrationEvent: Published) =
        FSharpValue.GetUnionFields(integrationEvent, typeof<Published>)
        |> fst
        |> _.Name
        |> createEventName
