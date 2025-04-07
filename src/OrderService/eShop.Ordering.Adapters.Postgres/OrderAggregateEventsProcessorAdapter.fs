[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderAggregateEventsProcessorAdapter

open System
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Domain.Ports
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
        let ofDomain (event: OrderAggregate.Event) : Event =
            match event with
            | OrderAggregate.Event.OrderStarted ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value }
                : OrderStartedEvent)
                |> Event.OrderStarted

            | OrderAggregate.Event.PaymentMethodVerified ev ->
                { BuyerId = ev.Buyer.Id |> BuyerId.value
                  BuyerName = ev.Buyer.Name |> BuyerName.value
                  CardTypeId = ev.VerifiedPaymentMethod.CardType.Id |> CardTypeId.value
                  CardTypeName = ev.VerifiedPaymentMethod.CardType.Name |> CardTypeName.value
                  CardNumber = ev.VerifiedPaymentMethod.CardNumber |> CardNumber.value
                  CardHolderName = ev.VerifiedPaymentMethod.CardHolderName |> CardHolderName.value
                  CardExpiration = ev.VerifiedPaymentMethod.CardExpiration }
                |> Event.PaymentMethodVerified

            | OrderAggregate.Event.OrderCancelled ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value }
                : OrderCancelledEvent)
                |> Event.OrderCancelled

            | OrderAggregate.Event.OrderStatusChangedToAwaitingValidation ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value
                   StockToValidate =
                     ev.StockToValidate
                     |> NonEmptyMap.toList
                     |> List.map (fun (id, units) ->
                         { Units = units |> Units.value
                           ProductId = id |> ProductId.value }) }
                : OrderStatusChangedToAwaitingValidationEvent)
                |> Event.OrderStatusChangedToAwaitingValidation

            | OrderAggregate.Event.OrderStockConfirmed ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value
                   ConfirmedOrderItems =
                     ev.ConfirmedOrderItems
                     |> NonEmptyMap.toList
                     |> List.map (fun (id, orderItem) ->
                         { Units = orderItem.Units |> Units.value
                           ProductId = id |> ProductId.value
                           Discount = orderItem.Discount |> Discount.value
                           PictureUrl = orderItem.PictureUrl
                           ProductName = orderItem.ProductName |> ProductName.value
                           UnitPrice = orderItem.UnitPrice |> UnitPrice.value }) }
                : OrderStockConfirmedEvent)
                |> Event.OrderStockConfirmed

            | OrderAggregate.Event.OrderPaid ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value
                   PaidOrderItems =
                     ev.PaidOrderItems
                     |> NonEmptyMap.toList
                     |> List.map (fun (id, orderItem) ->
                         { Units = orderItem.Units |> Units.value
                           ProductId = id |> ProductId.value
                           Discount = orderItem.Discount |> Discount.value
                           PictureUrl = orderItem.PictureUrl
                           ProductName = orderItem.ProductName |> ProductName.value
                           UnitPrice = orderItem.UnitPrice |> UnitPrice.value }) }
                : OrderPaidEvent)
                |> Event.OrderPaid

            | OrderAggregate.Event.OrderShipped ev ->
                ({ BuyerId = ev.Buyer.Id |> BuyerId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value
                   ShippedOrderItems =
                     ev.ShippedOrderItems
                     |> NonEmptyMap.toList
                     |> List.map (fun (id, orderItem) ->
                         { Units = orderItem.Units |> Units.value
                           ProductId = id |> ProductId.value
                           Discount = orderItem.Discount |> Discount.value
                           PictureUrl = orderItem.PictureUrl
                           ProductName = orderItem.ProductName |> ProductName.value
                           UnitPrice = orderItem.UnitPrice |> UnitPrice.value }) }
                : OrderShippedEvent)
                |> Event.OrderShipped

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

type PersistOrderAggregateEvents = OrderAggregateManagementPort.PersistOrderAggregateEvents<SqlIoError>

let persistOrderAggregateEvents dbSchema dbTransaction : PersistOrderAggregateEvents =
    Postgres.persistEvents Dto.Event.ofDomain dbSchema dbTransaction

type ReadUnprocessedOrderAggregateEvents = ReadUnprocessedEvents<OrderAggregate.State, OrderAggregate.Event, SqlIoError>

let readUnprocessedOrderAggregateEvents dbSchema sqlSession : ReadUnprocessedOrderAggregateEvents =
    Postgres.readUnprocessedEvents Dto.Event.toDomain dbSchema sqlSession
