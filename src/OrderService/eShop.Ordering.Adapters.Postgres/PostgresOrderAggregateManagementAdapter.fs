[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.PostgresOrderAggregateManagementAdapter

open System
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Ports
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres
open eShop.Prelude

module internal Dto =
    type OrderStatus =
        | Draft
        | AwaitingStockValidation
        | StockConfirmed
        | Paid
        | Shipped
        | Cancelled

    module OrderStatus =
        let private statusMap =
            Map.empty
            |> Map.add "Draft" Draft
            |> Map.add "AwaitingStockValidation" AwaitingStockValidation
            |> Map.add "StockConfirmed" StockConfirmed
            |> Map.add "Paid" Paid
            |> Map.add "Shipped" Shipped
            |> Map.add "Cancelled" Cancelled

        let create rawStatus =
            statusMap
            |> Map.tryFind rawStatus
            |> Result.requireSome $"Invalid OrderStatus: %s{rawStatus}"

        let toString status =
            statusMap |> Map.findKey (fun _ v -> v = status)

        let ofOrder =
            function
            | OrderAggregate.State.Init -> None
            | OrderAggregate.State.Draft _ -> OrderStatus.Draft |> Some
            | OrderAggregate.State.AwaitingStockValidation _ -> OrderStatus.AwaitingStockValidation |> Some
            | OrderAggregate.State.WithConfirmedStock _ -> OrderStatus.StockConfirmed |> Some
            | OrderAggregate.State.Paid _ -> OrderStatus.Paid |> Some
            | OrderAggregate.State.Shipped _ -> OrderStatus.Shipped |> Some
            | OrderAggregate.State.Cancelled _ -> OrderStatus.Cancelled |> Some

    [<CLIMutable>]
    type CardType = { Id: int; Name: string }

    [<CLIMutable>]
    type Order =
        { Id: int
          Status: string
          ItemProductId: int option
          ItemProductName: string option
          ItemUnitPrice: decimal option
          ItemUnits: int option
          ItemDiscount: decimal option
          ItemPictureUrl: string option
          BuyerId: Guid
          BuyerName: string option
          CardTypeId: int option
          CardTypeName: string option
          PaymentMethodCardNumber: string option
          PaymentMethodCardHolderName: string option
          PaymentMethodExpiration: DateOnly option
          Street: string option
          City: string option
          State: string option
          Country: string option
          ZipCode: string option
          StartedAt: DateTimeOffset option }

    module Order =
        let private createUnvalidatedOrderItem
            (dto: Order)
            : Result<(ProductId * UnvalidatedOrderItem) option, string> =
            dto.ItemProductId
            |> Option.traverseResult (fun productId ->
                validation {
                    let productId = productId |> ProductId.ofInt

                    let! productName =
                        dto.ItemProductName
                        |> Result.requireSome "Missing ProductName"
                        |> Result.bind ProductName.create

                    and! unitPrice =
                        dto.ItemUnitPrice
                        |> Result.requireSome "Missing UnitPrice"
                        |> Result.bind UnitPrice.create

                    and! units = dto.ItemUnits |> Result.requireSome "Missing Units" |> Result.bind Units.create

                    and! discount =
                        dto.ItemDiscount
                        |> Result.requireSome "Missing Discount"
                        |> Result.bind Discount.create

                    let unvalidatedOrderItem: UnvalidatedOrderItem =
                        { ProductName = productName
                          UnitPrice = unitPrice
                          Units = units
                          Discount = discount
                          PictureUrl = dto.ItemPictureUrl }

                    return productId, unvalidatedOrderItem
                }
                |> Result.mapError (String.concat "; "))

        let private createOrderItemWithUnconfirmedStock
            (dto: Order)
            : Result<(ProductId * OrderItemWithUnconfirmedStock) option, string> =
            resultOption {
                let! productId, unvalidatedOrderItem = createUnvalidatedOrderItem dto

                let! validatedOrderItem =
                    unvalidatedOrderItem
                    |> UnvalidatedOrderItem.validate
                    |> Result.mapError (fun (_: DiscountHigherThanTotalPriceError) ->
                        "Discount cannot be higher than a total price")

                return productId, validatedOrderItem
            }

        let private createOrderItemWithConfirmedStock =
            createOrderItemWithUnconfirmedStock
            >> ResultOption.map (Tuple.mapSnd OrderItemWithUnconfirmedStock.confirmStock)

        let private createAddress (dto: Order) =
            validation {
                let! street = dto.Street |> Result.requireSome "Missing Street" |> Result.bind Street.create
                and! city = dto.City |> Result.requireSome "Missing City" |> Result.bind City.create
                and! state = dto.State |> Result.requireSome "Missing State" |> Result.bind Street.create

                and! zipCode = dto.Street |> Result.requireSome "Missing ZipCode" |> Result.bind Street.create

                and! country =
                    dto.Country
                    |> Result.requireSome "Missing Country"
                    |> Result.bind Country.create

                return
                    ({ Street = street
                       City = city
                       State = state
                       Country = country
                       ZipCode = zipCode }
                    : Address)
            }
            |> Result.mapError (String.concat "; ")

        let private createVerifiedPaymentMethod (dto: Order) =
            validation {
                let! cardTypeId =
                    dto.CardTypeId
                    |> Result.requireSome "Missing CardTypeId"
                    |> Result.map CardTypeId.ofInt

                and! cardTypeName =
                    dto.CardTypeName
                    |> Result.requireSome "Missing CardTypeName"
                    |> Result.bind CardTypeName.create

                and! cardNumber =
                    dto.PaymentMethodCardNumber
                    |> Result.requireSome "Missing CardNumber"
                    |> Result.bind CardNumber.create

                and! cardHolderName =
                    dto.PaymentMethodCardHolderName
                    |> Result.requireSome "Missing CardHolderName"
                    |> Result.bind CardHolderName.create

                and! expiration = dto.PaymentMethodExpiration |> Result.requireSome "Missing Expiration"

                return
                    ({ CardType = { Id = cardTypeId; Name = cardTypeName }
                       CardNumber = cardNumber
                       CardHolderName = cardHolderName
                       CardExpiration = expiration }
                    : VerifiedPaymentMethod)
            }
            |> Result.mapError (String.concat "; ")

        let private createBuyer (dto: Order) =
            dto.BuyerName
            |> Result.requireSome "Missing BuyerName"
            |> Result.bind BuyerName.create
            |> Result.map (fun buyerName ->
                ({ Id = dto.BuyerId |> BuyerId.ofGuid
                   Name = buyerName }
                : Buyer))

        let toDomain (maybeOrder: (int * Order seq) option) : Result<OrderAggregate.State, string> =
            maybeOrder
            |> Option.map (fun (_, orders) ->
                let orderDto = orders |> Seq.head
                let buyerId = orderDto.BuyerId |> BuyerId.ofGuid

                orderDto.Status
                |> OrderStatus.create
                |> Result.mapError List.singleton
                |> Result.bind (function
                    | Draft ->
                        orders
                        |> List.ofSeq
                        |> List.traverseResultA createUnvalidatedOrderItem
                        |> Result.map (
                            Seq.choose id
                            >> Map.ofSeq
                            >> fun items ->
                                ({ BuyerId = buyerId
                                   UnvalidatedOrderItems = items }
                                : OrderAggregate.State.Draft)
                                |> OrderAggregate.State.Draft
                        )
                    | AwaitingStockValidation ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithUnconfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Seq.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   UnconfirmedOrderItems = orderItems }
                                : OrderAggregate.State.AwaitingStockValidation)
                                |> OrderAggregate.State.AwaitingStockValidation
                        }
                    | StockConfirmed ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Seq.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   ConfirmedOrderItems = orderItems }
                                : OrderAggregate.State.WithConfirmedStock)
                                |> OrderAggregate.State.WithConfirmedStock
                        }
                    | Paid ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Seq.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   PaidOrderItems = orderItems }
                                : OrderAggregate.State.Paid)
                                |> OrderAggregate.State.Paid
                        }
                    | Shipped ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Seq.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   ShippedOrderItems = orderItems }
                                : OrderAggregate.State.Shipped)
                                |> OrderAggregate.State.Shipped
                        }
                    | Cancelled ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createUnvalidatedOrderItem
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Seq.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   Address = address
                                   StartedAt = startedAt
                                   CancelledOrderItems = orderItems }
                                : OrderAggregate.State.Cancelled)
                                |> OrderAggregate.State.Cancelled
                        }))
            |> Option.defaultValue (OrderAggregate.State.Init |> Ok)
            |> Result.mapError (String.concat "; ")

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

module private Sql =
    let getSupportedCardTypes (DbSchema schema) =
        $"""
        SELECT "Id", "Name",
        FROM "{schema}"."CardTypes"
        """

    let getOrderById (DbSchema schema) =
        $"""
        SELECT
            orders."Id", orders."Status", orders."Street", orders."City", orders."State", orders."Country", orders."ZipCode", orders."StartedAt",
            items."ProductId" AS "ItemProductId", items."ProductName" AS "ItemProductName", items."UnitPrice" AS "ItemUnitPrice",
            items."Units" AS "ItemUnits", items."Discount" AS "ItemDiscount", items."PictureUrl" AS "ItemPictureUrl",
            buyers."Id" AS "BuyerId", buyers."Name" AS "BuyerName",
            cardTypes."Id" AS "CardTypeId", cardTypes."Name" AS "CardTypeName",
            paymentMethods."CardNumber" AS "PaymentMethodCardNumber", paymentMethods."CardHolderName" AS "PaymentMethodCardHolderName",
            paymentMethods."Expiration" AS "PaymentMethodExpiration"
        FROM "{schema}"."Orders" AS orders
        LEFT JOIN "{schema}"."OrderItems" AS items ON items."OrderId" = orders."Id"
        LEFT JOIN "{schema}"."Buyers" AS buyers ON buyers."Id" = orders."BuyerId"
        LEFT JOIN "{schema}"."PaymentMethods" AS paymentMethods ON paymentMethods."Id" = orders."PaymentMethodId"
        LEFT JOIN "{schema}"."CardTypes" AS cardTypes ON cardTypes."Id" = paymentMethods."CardTypeId"
        WHERE orders."Id" = @OrderId
        """

    let getOrAddPaymentMethod (DbSchema schema) =
        $"""
        WITH incoming AS (
            MERGE INTO "{schema}"."PaymentMethods" AS existing
            USING (VALUES (@BuyerId, @CardTypeId, @CardNumber, @CardHolderName, @CardExpiration))
                AS pt("BuyerId", "CardTypeId", "CardNumber", "CardHolderName", "CardExpiration")
                ON pt."BuyerId" = existing."BuyerId"
                    AND pt."CardTypeId" = existing."CardTypeId"
                    AND pt."CardNumber" = existing."CardNumber"
                    AND pt."CardHolderName" = existing."CardHolderName"
                    AND pt."CardExpiration" = existing."CardExpiration"
            WHEN MATCHED THEN DO NOTHING
            WHEN NOT MATCHED THEN INSERT ("BuyerId", "CardTypeId", "CardNumber", "CardHolderName", "CardExpiration")
                VALUES (pt."BuyerId", pt."CardTypeId", pt."CardNumber", pt."CardHolderName", pt."CardExpiration")
            RETURNING existing."Id"
        )
        SELECT "Id" FROM incoming
        UNION ALL
        SELECT "Id" FROM "{schema}"."PaymentMethods"
        WHERE
            "BuyerId" = @BuyerId
            AND "CardTypeId" = @CardTypeId
            AND "CardNumber" = @CardNumber
            AND "CardHolderName" = @CardHolderName
            AND "Expiration" = @Expiration
        """

    let upsertBuyer (DbSchema schema) =
        $"""
        INSERT INTO "{schema}"."Buyers" ("Id", "Name") VALUES (@BuyerId, @BuyerName)
        ON CONFLICT ("Id")
        DO UPDATE SET "Name" = COALESCE(EXCLUDED."Name", "Name")
        """

    let upsertOrderItem (DbSchema schema) =
        $"""
        INSERT INTO "{schema}"."OrderItems" ("ProductId", "OrderId", "ProductName", "UnitPrice", "Units", "Discount", "PictureUrl")
        VALUES (@ProductId, @OrderId, @ProductName, @UnitPrice, @Units, @Discount, @PictureUrl)
        ON CONFLICT ("ProductId", "OrderId")
        DO UPDATE SET
            "ProductName" = EXCLUDED."ProductName", "UnitPrice" = EXCLUDED."UnitPrice",
            "Units" = EXCLUDED."Units", "Discount" = EXCLUDED."Discount", "PictureUrl" = EXCLUDED."PictureUrl"
        """

    let upsertOrder (DbSchema schema) =
        $"""
        INSERT INTO "{schema}"."Orders" ("Id", "BuyerId", "PaymentMethodId", "Status", "StartedAt", "Street", "City", "State", "Country", "ZipCode")
        VALUES (@Id, @BuyerId, @PaymentMethodId, @Status, @StartedAt, @Street, @City, @State, @Country, @ZipCode)
        ON CONFLICT ("Id")
        DO UPDATE SET
            "BuyerId" = EXCLUDED."BuyerId", "PaymentMethodId" = EXCLUDED."PaymentMethodId",
            "Status" = EXCLUDED."Status", "StartedAt" = EXCLUDED."StartedAt", "Street" = EXCLUDED."Street",
            "City" = EXCLUDED."City", "State" = EXCLUDED."State", "Country" = EXCLUDED."Country", "ZipCode" = EXCLUDED."ZipCode"
        """

type ReadOrderAggregate = OrderAggregateManagementPort.ReadOrderAggregate<SqlIoError>

let readOrderAggregate dbSchema sqlSession : ReadOrderAggregate =
    fun (AggregateId aggregateId) ->
        asyncResult {
            let! orderDtos =
                {| OrderId = aggregateId |}
                |> Dapper.query<Dto.Order> sqlSession (Sql.getOrderById dbSchema)
                |> AsyncResult.map (Seq.groupBy _.Id)

            return!
                orderDtos
                |> Result.requireSingleOrEmpty
                |> Result.mapError InvalidData
                |> Result.bind (Dto.Order.toDomain >> Result.mapError InvalidData)
        }

type PersistOrderAggregate = OrderAggregateManagementPort.PersistOrderAggregate<SqlIoError>

let persistOrderAggregate dbSchema dbTransaction : PersistOrderAggregate =
    fun (AggregateId aggregateId) order ->
        asyncResult {
            let sqlSession = dbTransaction |> SqlSession.Sustained

            do!
                order
                |> OrderAggregate.State.getBuyerId
                |> Option.map (fun buyerId ->
                    {| BuyerId = buyerId |> BuyerId.value
                       BuyerName = order |> OrderAggregate.State.getBuyerName |> Option.map BuyerName.value |> Option.toObj |}
                    |> Dapper.execute sqlSession (Sql.upsertBuyer dbSchema)
                    |> AsyncResult.ignore)
                |> Option.defaultValue (AsyncResult.ok ())

            let! maybePaymentMethodId =
                order
                |> OrderAggregate.State.getPaymentMethod
                |> Option.map (fun paymentMethod ->
                    order
                    |> OrderAggregate.State.getBuyerId
                    |> Result.requireSome ("Missing BuyerId for existing Payment Method" |> InvalidData)
                    |> AsyncResult.ofResult
                    |> AsyncResult.bind (fun buyerId ->
                        {| BuyerId = buyerId |> BuyerId.value
                           CardTypeId = paymentMethod.CardType.Id |> CardTypeId.value
                           CardNumber = paymentMethod.CardNumber |> CardNumber.value
                           CardHolderName = paymentMethod.CardHolderName |> CardHolderName.value
                           CardExpiration = paymentMethod.CardExpiration |}
                        |> Dapper.executeScalar<Guid> sqlSession (Sql.getOrAddPaymentMethod dbSchema)
                        |> AsyncResult.map Some))
                |> Option.defaultValue (AsyncResult.ok None)

            do!
                order
                |> Dto.OrderStatus.ofOrder
                |> Option.map (fun status ->
                    let maybeAddress = order |> OrderAggregate.State.getAddress

                    {| Id = aggregateId
                       BuyerId = order |> OrderAggregate.State.getBuyerId |> Option.map BuyerId.value |> Option.toNullable
                       PaymentMethodId = maybePaymentMethodId |> Option.toNullable
                       Status = status |> Dto.OrderStatus.toString
                       StartedAt = order |> OrderAggregate.State.getStartedAt |> Option.toNullable
                       Street = maybeAddress |> Option.map (_.Street >> Street.value) |> Option.toObj
                       City = maybeAddress |> Option.map (_.City >> City.value) |> Option.toObj
                       Country = maybeAddress |> Option.map (_.Country >> Country.value) |> Option.toObj
                       ZipCode = maybeAddress |> Option.map (_.ZipCode >> ZipCode.value) |> Option.toObj |}
                    |> Dapper.execute sqlSession (Sql.upsertOrder dbSchema)
                    |> AsyncResult.ignore)
                |> Option.defaultValue (AsyncResult.ok ())

            do!
                order
                |> OrderAggregate.State.getOrderItems
                |> Map.toList
                |> List.traverseAsyncResultM (fun (id, item) ->
                    {| ProductId = id |> ProductId.value
                       OrderId = aggregateId
                       ProductName = item |> OrderItem.getProductName |> ProductName.value
                       UnitPrice = item |> OrderItem.getUnitPrice |> UnitPrice.value
                       Units = item |> OrderItem.getUnits |> Units.value
                       Discount = item |> OrderItem.getDiscount |> Discount.value
                       PictureUrl = item |> OrderItem.getPictureUrl |> Option.toObj |}
                    |> Dapper.execute sqlSession (Sql.upsertOrderItem dbSchema))
                |> AsyncResult.ignore
        }

type GetSupportedCardTypes = OrderAggregateManagementPort.GetSupportedCardTypes<SqlIoError>

let getSupportedCardTypes dbSchema sqlSession : GetSupportedCardTypes =
    fun () ->
        asyncResult {
            let! cardTypeDtos = Dapper.query<Dto.CardType> sqlSession (Sql.getSupportedCardTypes dbSchema) null

            return!
                cardTypeDtos
                |> Seq.traverseResultA (fun dto ->
                    dto.Name
                    |> CardTypeName.create
                    |> Result.map (fun name -> dto.Id |> CardTypeId.ofInt, name))
                |> Result.map (Map.ofSeq >> SupportedCardTypes)
                |> Result.mapError (String.concat "; " >> InvalidData)
        }

type PersistOrderAggregateEvents = OrderAggregateManagementPort.PersistOrderAggregateEvents<Postgres.EventId, SqlIoError>

let persistOrderAggregateEvents dbSchema dbTransaction : PersistOrderAggregateEvents =
    Postgres.persistEvents Dto.Event.ofDomain dbSchema dbTransaction

type ReadUnprocessedOrderAggregateEvents = ReadUnprocessedEvents<OrderAggregate.State, Postgres.EventId, OrderAggregate.Event, SqlIoError>

let readUnprocessedOrderAggregateEvents dbSchema sqlSession : ReadUnprocessedOrderAggregateEvents =
    Postgres.readUnprocessedEvents Dto.Event.toDomain dbSchema sqlSession

type OrderAggregateEventsProcessor<'eventHandlerIoError> =
    EventsProcessor<OrderAggregate.State, Postgres.EventId, OrderAggregate.Event, SqlIoError, 'eventHandlerIoError>
