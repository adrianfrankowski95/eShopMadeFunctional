﻿[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderAggregateEventsProcessorAdapter

open System
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

module internal Dto =
    type OrderItem =
        { ProductId: int
          ProductName: string
          UnitPrice: decimal
          Units: int
          Discount: decimal
          PictureUrl: string option }

    type StockToValidate = { ProductId: int; Units: int }

    type OrderStartedEvent = { BuyerId: Guid; BuyerName: string }

    type PaymentMethodVerifiedEvent =
        { BuyerId: Guid
          BuyerName: string
          CardTypeId: int
          CardTypeName: string
          CardNumber: string
          CardHolderName: string
          CardExpiration: DateOnly }

    type OrderCancelledEvent = { BuyerId: Guid; BuyerName: string }

    type OrderStatusChangedToAwaitingValidationEvent =
        { BuyerId: Guid
          BuyerName: string
          StockToValidate: StockToValidate list }

    type OrderStockConfirmedEvent =
        { BuyerId: Guid
          BuyerName: string
          ConfirmedOrderItems: OrderItem list }

    type OrderPaidEvent =
        { BuyerId: Guid
          BuyerName: string
          PaidOrderItems: OrderItem list }

    type OrderShippedEvent =
        { BuyerId: Guid
          BuyerName: string
          ShippedOrderItems: OrderItem list }

    type Event =
        | OrderStarted of OrderStartedEvent
        | PaymentMethodVerified of PaymentMethodVerifiedEvent
        | OrderCancelled of OrderCancelledEvent
        | OrderStatusChangedToAwaitingValidation of OrderStatusChangedToAwaitingValidationEvent
        | OrderStockConfirmed of OrderStockConfirmedEvent
        | OrderPaid of OrderPaidEvent
        | OrderShipped of OrderShippedEvent

    module Event =
        let toDomain (dto: Event) : Result<OrderAggregate.Event, string> =
            match dto with
            | OrderStarted ev ->
                ev.BuyerName
                |> BuyerName.create
                |> Result.mapError List.singleton
                |> Result.map (fun buyerName ->
                    ({ Buyer =
                        { Id = ev.BuyerId |> BuyerId.ofGuid
                          Name = buyerName } }
                    : OrderAggregate.Event.OrderStarted)
                    |> OrderAggregate.Event.OrderStarted)

            | PaymentMethodVerified ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid
                    let cardTypeId = ev.CardTypeId |> CardTypeId.ofInt

                    let! buyerName = ev.BuyerName |> BuyerName.create
                    and! cardNumber = ev.CardNumber |> CardNumber.create
                    and! cardTypeName = ev.CardTypeName |> CardTypeName.create
                    and! cardHolderName = ev.CardHolderName |> CardHolderName.create

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName }
                           VerifiedPaymentMethod =
                             { CardType = { Id = cardTypeId; Name = cardTypeName }
                               CardExpiration = ev.CardExpiration
                               CardNumber = cardNumber
                               CardHolderName = cardHolderName } }
                        : OrderAggregate.Event.PaymentMethodVerified)
                        |> OrderAggregate.Event.PaymentMethodVerified
                }

            | OrderCancelled ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName } }: OrderAggregate.Event.OrderCancelled)
                        |> OrderAggregate.Event.OrderCancelled
                }

            | OrderStatusChangedToAwaitingValidation ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    and! stockToValidate =
                        ev.StockToValidate
                        |> List.traverseResultA (fun stock ->
                            stock.Units
                            |> Units.create
                            |> Result.map (fun units -> stock.ProductId |> ProductId.ofInt, units))
                        |> Result.bind (
                            NonEmptyMap.ofSeq
                            >> Result.mapError ((+) "Invalid StockToValidate: " >> List.singleton)
                        )

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName }
                           StockToValidate = stockToValidate }
                        : OrderAggregate.Event.OrderStatusChangedToAwaitingValidation)
                        |> OrderAggregate.Event.OrderStatusChangedToAwaitingValidation
                }

            | OrderStockConfirmed ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    and! orderItems =
                        ev.ConfirmedOrderItems
                        |> List.traverseResultA (fun orderItem ->
                            validation {
                                let! productName = orderItem.ProductName |> ProductName.create
                                and! unitPrice = orderItem.UnitPrice |> UnitPrice.create
                                and! units = orderItem.Units |> Units.create
                                and! discount = orderItem.Discount |> Discount.create

                                return
                                    orderItem.ProductId |> ProductId.ofInt,
                                    ({ Discount = discount
                                       Units = units
                                       PictureUrl = orderItem.PictureUrl
                                       ProductName = productName
                                       UnitPrice = unitPrice }
                                    : OrderItemWithConfirmedStock)
                            }
                            |> Result.mapError (String.concat "; "))
                        |> Result.bind (
                            NonEmptyMap.ofSeq
                            >> Result.mapError ((+) "Invalid ConfirmedOrderItems: " >> List.singleton)
                        )

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName }
                           ConfirmedOrderItems = orderItems }
                        : OrderAggregate.Event.OrderStockConfirmed)
                        |> OrderAggregate.Event.OrderStockConfirmed
                }

            | OrderPaid ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    and! orderItems =
                        ev.PaidOrderItems
                        |> List.traverseResultA (fun orderItem ->
                            validation {
                                let! productName = orderItem.ProductName |> ProductName.create
                                and! unitPrice = orderItem.UnitPrice |> UnitPrice.create
                                and! units = orderItem.Units |> Units.create
                                and! discount = orderItem.Discount |> Discount.create

                                return
                                    orderItem.ProductId |> ProductId.ofInt,
                                    ({ Discount = discount
                                       Units = units
                                       PictureUrl = orderItem.PictureUrl
                                       ProductName = productName
                                       UnitPrice = unitPrice }
                                    : OrderItemWithConfirmedStock)
                            }
                            |> Result.mapError (String.concat "; "))
                        |> Result.bind (
                            NonEmptyMap.ofSeq
                            >> Result.mapError ((+) "Invalid PaidOrderItems: " >> List.singleton)
                        )

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName }
                           PaidOrderItems = orderItems }
                        : OrderAggregate.Event.OrderPaid)
                        |> OrderAggregate.Event.OrderPaid
                }

            | OrderShipped ev ->
                validation {
                    let buyerId = ev.BuyerId |> BuyerId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    and! orderItems =
                        ev.ShippedOrderItems
                        |> List.traverseResultA (fun orderItem ->
                            validation {
                                let! productName = orderItem.ProductName |> ProductName.create
                                and! unitPrice = orderItem.UnitPrice |> UnitPrice.create
                                and! units = orderItem.Units |> Units.create
                                and! discount = orderItem.Discount |> Discount.create

                                return
                                    orderItem.ProductId |> ProductId.ofInt,
                                    ({ Discount = discount
                                       Units = units
                                       PictureUrl = orderItem.PictureUrl
                                       ProductName = productName
                                       UnitPrice = unitPrice }
                                    : OrderItemWithConfirmedStock)
                            }
                            |> Result.mapError (String.concat "; "))
                        |> Result.bind (
                            NonEmptyMap.ofSeq
                            >> Result.mapError ((+) "Invalid ShippedOrderItems: " >> List.singleton)
                        )

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName }
                           ShippedOrderItems = orderItems }
                        : OrderAggregate.Event.OrderShipped)
                        |> OrderAggregate.Event.OrderShipped
                }
            |> Result.mapError (String.concat "; ")

type ReadUnprocessedOrderAggregateEvents =
    ReadUnprocessedEvents<OrderAggregate.State, Postgres.EventId, OrderAggregate.Event, SqlIoError>

let readUnprocessedOrderAggregateEvents dbSchema sqlSession : ReadUnprocessedOrderAggregateEvents =
    Postgres.readUnprocessedEvents Dto.Event.toDomain dbSchema sqlSession
