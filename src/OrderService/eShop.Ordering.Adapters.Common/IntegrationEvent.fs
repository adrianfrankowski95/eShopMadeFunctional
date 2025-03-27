[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.RabbitMQ.IntegrationEvent

open System.Text.Json
open Microsoft.FSharp.Reflection
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.RabbitMQ
open eShop.Prelude
open eShop.Ordering.Domain.Model

// Note: It must stay in line with C# event names until we get rid of legacy code
let private createEventName value =
    value + "IntegrationEvent" |> RabbitMQ.EventName.create

let private forceEventName = createEventName >> Result.valueOr failwith

[<RequireQualifiedAccess>]
module Consumed =
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

    type T =
        | GracePeriodConfirmed of GracePeriodConfirmed
        | OrderStockConfirmed of OrderStockConfirmed
        | OrderStockRejected of OrderStockRejected
        | OrderPaymentFailed of OrderPaymentFailed
        | OrderPaymentSucceeded of OrderPaymentSucceeded

    let private nameTypeMap =
        FSharpType.GetUnionCases(typeof<T>)
        |> Array.map (fun caseInfo ->
            let eventName = caseInfo.Name |> forceEventName
            let unionCaseType = caseInfo.GetFields() |> Array.head |> _.PropertyType

            eventName, (unionCaseType, caseInfo))
        |> Map.ofArray

    let names = nameTypeMap |> Map.keys |> Set.ofSeq

    let deserialize (jsonOptions: JsonSerializerOptions) (eventName: RabbitMQ.EventName) (json: string) =
        let deserialize targetType =
            JsonSerializer.Deserialize(json, targetType, jsonOptions)

        let createUnion (data, targetUnionCase) =
            FSharpValue.MakeUnion(targetUnionCase, [| data |]) :?> T

        fun () -> nameTypeMap |> Map.find eventName |> Tuple.mapFst deserialize |> createUnion
        |> Result.catch

    let getOrderId (integrationEvent: T) =
        match integrationEvent with
        | GracePeriodConfirmed ev -> ev.OrderId
        | OrderStockConfirmed ev -> ev.OrderId
        | OrderStockRejected ev -> ev.OrderId
        | OrderPaymentFailed ev -> ev.OrderId
        | OrderPaymentSucceeded ev -> ev.OrderId
        |> AggregateId.ofInt<Order.State>

type Consumed = Consumed.T

[<RequireQualifiedAccess>]
module Published =
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

    type T =
        | OrderStatusChangedToStarted of OrderStatusChangedToStarted
        | OrderStatusChangedToCancelled of OrderStatusChangedToCancelled
        | OrderStatusChangedToShipped of OrderStatusChangedToShipped
        | OrderStatusChangedToAwaitingValidation of OrderStatusChangedToAwaitingValidation
        | OrderStatusChangedToPaid of OrderStatusChangedToPaid
        | OrderStatusChangedToStockConfirmed of OrderStatusChangedToStockConfirmed
        | OrderStatusChangedToSubmitted of OrderStatusChangedToSubmitted

    let ofDomain (AggregateId aggregateId) (domainEvent: Order.Event) =
        match domainEvent with
        | Order.Event.OrderStarted ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStarted)
            |> T.OrderStatusChangedToStarted

        | Order.Event.PaymentMethodVerified ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToSubmitted)
            |> T.OrderStatusChangedToSubmitted

        | Order.Event.OrderCancelled ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToCancelled)
            |> T.OrderStatusChangedToCancelled

        | Order.Event.OrderStatusChangedToAwaitingValidation ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.StockToValidate
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, units) ->
                     { ProductId = productId |> ProductId.toString
                       Units = units |> Units.value }) }
            : OrderStatusChangedToAwaitingValidation)
            |> T.OrderStatusChangedToAwaitingValidation

        | Order.Event.OrderStockConfirmed ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToStockConfirmed)
            |> T.OrderStatusChangedToStockConfirmed

        | Order.Event.OrderPaid ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString
               OrderStockItems =
                 ev.PaidOrderItems
                 |> NonEmptyMap.toList
                 |> List.map (fun (productId, orderItem) ->
                     { ProductId = productId |> ProductId.toString
                       Units = orderItem.Units |> Units.value }) }
            : OrderStatusChangedToPaid)
            |> T.OrderStatusChangedToPaid

        | Order.Event.OrderShipped ev ->
            ({ OrderId = aggregateId
               BuyerName = ev.Buyer.Name |> BuyerName.value
               BuyerIdentityGuid = ev.Buyer.Id |> BuyerId.toString }
            : OrderStatusChangedToShipped)
            |> T.OrderStatusChangedToShipped

    let serialize (jsonOptions: JsonSerializerOptions) (integrationEvent: T) =
        let inline serialize ev =
            JsonSerializer.SerializeToUtf8Bytes(ev, jsonOptions)

        fun () ->
            match integrationEvent with
            | OrderStatusChangedToStarted ev -> ev |> serialize
            | OrderStatusChangedToCancelled ev -> ev |> serialize
            | OrderStatusChangedToShipped ev -> ev |> serialize
            | OrderStatusChangedToAwaitingValidation ev -> ev |> serialize
            | OrderStatusChangedToPaid ev -> ev |> serialize
            | OrderStatusChangedToStockConfirmed ev -> ev |> serialize
            | OrderStatusChangedToSubmitted ev -> ev |> serialize
        |> Result.catch

    let getEventName (integrationEvent: T) =
        FSharpValue.GetUnionFields(integrationEvent, typeof<T>)
        |> fst
        |> _.Name
        |> createEventName

type Published = Published.T
