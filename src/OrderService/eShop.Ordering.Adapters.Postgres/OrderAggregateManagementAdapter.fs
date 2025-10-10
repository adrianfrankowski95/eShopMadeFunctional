[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderAggregateManagementAdapter

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
        | Submitted
        | AwaitingStockValidation
        | StockConfirmed
        | Paid
        | Shipped
        | Cancelled

    module OrderStatus =
        let private statusMap =
            Map.empty
            |> Map.add "Draft" Draft
            |> Map.add "Submitted" Submitted
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
            | Order.State.Init -> None
            | Order.State.Draft _ -> OrderStatus.Draft |> Some
            | Order.State.Submitted _ -> OrderStatus.Submitted |> Some
            | Order.State.AwaitingStockValidation _ -> OrderStatus.AwaitingStockValidation |> Some
            | Order.State.WithConfirmedStock _ -> OrderStatus.StockConfirmed |> Some
            | Order.State.Paid _ -> OrderStatus.Paid |> Some
            | Order.State.Shipped _ -> OrderStatus.Shipped |> Some
            | Order.State.Cancelled _ -> OrderStatus.Cancelled |> Some

    [<CLIMutable>]
    type Order =
        { Id: Guid
          Status: string
          Description: string option
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
          PaymentMethodCardExpiration: DateOnly option
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

                and! expiration = dto.PaymentMethodCardExpiration |> Result.requireSome "Missing Expiration"

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
                ({ Id = dto.BuyerId |> UserId.ofGuid
                   Name = buyerName }
                : Buyer))

        let toDomain (maybeOrder: (Guid * Order seq) option) : Result<Order.State, string> =
            maybeOrder
            |> Option.map (fun (_, orders) ->
                let orderDto = orders |> Seq.head
                let buyerId = orderDto.BuyerId |> UserId.ofGuid

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
                                : Order.State.Draft)
                                |> Order.State.Draft
                        )
                    | Submitted ->
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
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   UnconfirmedOrderItems = orderItems }
                                : Order.State.Submitted)
                                |> Order.State.Submitted
                        }
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
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   UnconfirmedOrderItems = orderItems }
                                : Order.State.AwaitingStockValidation)
                                |> Order.State.AwaitingStockValidation
                        }
                    | StockConfirmed ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! description =
                                orderDto.Description
                                |> Result.requireSome "Missing Description"
                                |> Result.bind Description.create

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   ConfirmedOrderItems = orderItems
                                   Description = description }
                                : Order.State.WithConfirmedStock)
                                |> Order.State.WithConfirmedStock
                        }
                    | Paid ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! description =
                                orderDto.Description
                                |> Result.requireSome "Missing Description"
                                |> Result.bind Description.create

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   PaidOrderItems = orderItems
                                   Description = description }
                                : Order.State.Paid)
                                |> Order.State.Paid
                        }
                    | Shipped ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! paymentMethod = orderDto |> createVerifiedPaymentMethod
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! description =
                                orderDto.Description
                                |> Result.requireSome "Missing Description"
                                |> Result.bind Description.create

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createOrderItemWithConfirmedStock
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   PaymentMethod = paymentMethod
                                   Address = address
                                   StartedAt = startedAt
                                   ShippedOrderItems = orderItems
                                   Description = description }
                                : Order.State.Shipped)
                                |> Order.State.Shipped
                        }
                    | Cancelled ->
                        validation {
                            let! buyer = orderDto |> createBuyer
                            and! address = orderDto |> createAddress
                            and! startedAt = orderDto.StartedAt |> Result.requireSome "Missing StartedAt"

                            and! description =
                                orderDto.Description
                                |> Result.requireSome "Missing Description"
                                |> Result.bind Description.create

                            and! orderItems =
                                orders
                                |> Seq.traverseResultA createUnvalidatedOrderItem
                                |> Result.bind (
                                    Seq.choose id
                                    >> NonEmptyMap.ofSeq
                                    >> Result.mapError ((+) "Invalid OrderItems: " >> Array.singleton)
                                )
                                |> Result.mapError (String.concat "; ")

                            return
                                ({ Buyer = buyer
                                   Address = address
                                   StartedAt = startedAt
                                   CancelledOrderItems = orderItems
                                   Description = description }
                                : Order.State.Cancelled)
                                |> Order.State.Cancelled
                        }))
            |> Option.defaultValue (Order.State.Init |> Ok)
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
        let ofDomain (event: Order.Event) : Event =
            match event with
            | Order.Event.OrderStarted ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value }
                : OrderStartedEvent)
                |> Event.OrderStarted

            | Order.Event.PaymentMethodVerified ev ->
                { BuyerId = ev.Buyer.Id |> UserId.value
                  BuyerName = ev.Buyer.Name |> BuyerName.value
                  CardTypeId = ev.VerifiedPaymentMethod.CardType.Id |> CardTypeId.value
                  CardTypeName = ev.VerifiedPaymentMethod.CardType.Name |> CardTypeName.value
                  CardNumber = ev.VerifiedPaymentMethod.CardNumber |> CardNumber.value
                  CardHolderName = ev.VerifiedPaymentMethod.CardHolderName |> CardHolderName.value
                  CardExpiration = ev.VerifiedPaymentMethod.CardExpiration }
                |> Event.PaymentMethodVerified

            | Order.Event.OrderCancelled ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value }
                : OrderCancelledEvent)
                |> Event.OrderCancelled

            | Order.Event.OrderStatusChangedToAwaitingValidation ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
                   BuyerName = ev.Buyer.Name |> BuyerName.value
                   StockToValidate =
                     ev.StockToValidate
                     |> NonEmptyMap.toList
                     |> List.map (fun (id, units) ->
                         { Units = units |> Units.value
                           ProductId = id |> ProductId.value }) }
                : OrderStatusChangedToAwaitingValidationEvent)
                |> Event.OrderStatusChangedToAwaitingValidation

            | Order.Event.OrderStockConfirmed ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
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

            | Order.Event.OrderPaid ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
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

            | Order.Event.OrderShipped ev ->
                ({ BuyerId = ev.Buyer.Id |> UserId.value
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

        let toDomain (dto: Event) : Result<Order.Event, string> =
            match dto with
            | OrderStarted ev ->
                ev.BuyerName
                |> BuyerName.create
                |> Result.mapError List.singleton
                |> Result.map (fun buyerName ->
                    ({ Buyer =
                        { Id = ev.BuyerId |> UserId.ofGuid
                          Name = buyerName } }
                    : Order.Event.OrderStarted)
                    |> Order.Event.OrderStarted)

            | PaymentMethodVerified ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid
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
                        : Order.Event.PaymentMethodVerified)
                        |> Order.Event.PaymentMethodVerified
                }

            | OrderCancelled ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid

                    let! buyerName = ev.BuyerName |> BuyerName.create

                    return
                        ({ Buyer = { Id = buyerId; Name = buyerName } }: Order.Event.OrderCancelled)
                        |> Order.Event.OrderCancelled
                }

            | OrderStatusChangedToAwaitingValidation ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid

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
                        : Order.Event.OrderStatusChangedToAwaitingValidation)
                        |> Order.Event.OrderStatusChangedToAwaitingValidation
                }

            | OrderStockConfirmed ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid

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
                        : Order.Event.OrderStockConfirmed)
                        |> Order.Event.OrderStockConfirmed
                }

            | OrderPaid ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid

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
                        : Order.Event.OrderPaid)
                        |> Order.Event.OrderPaid
                }

            | OrderShipped ev ->
                validation {
                    let buyerId = ev.BuyerId |> UserId.ofGuid

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
                        : Order.Event.OrderShipped)
                        |> Order.Event.OrderShipped
                }
            |> Result.mapError (String.concat "; ")

module private Sql =
    let getOrderById (DbSchema schema) =
        $"""
        SELECT
            orders.id as "Id", orders.status as "Status", orders.description as "Description",
            items.product_id as "ItemProductId", items.product_name as "ItemProductName",
            items.unit_price as "ItemUnitPrice", items.units as "ItemUnits", items.discount as "ItemDiscount",
            items.picture_url as "ItemPictureUrl", buyers.id as "BuyerId", buyers.name as "BuyerName",
            card_types.id as "CardTypeId", card_types.name as "CardTypeName",
            payment_methods.card_number as "PaymentMethodCardNumber",
            payment_methods.card_holder_name as "PaymentMethodCardHolderName",
            payment_methods.card_expiration as "PaymentMethodCardExpiration",
            orders.street as "Street", orders.city as "City", orders.state as "State", orders.country as "Country",
            orders.zip_code as "ZipCode", orders.started_at as "StartedAt"
        FROM "%s{schema}".orders AS orders
        LEFT JOIN "%s{schema}".order_items AS items ON items.order_id = orders.id
        LEFT JOIN "%s{schema}".buyers AS buyers ON buyers.id = orders.buyer_id
        LEFT JOIN "%s{schema}".payment_methods AS payment_methods ON payment_methods.id = orders.payment_method_id
        LEFT JOIN "%s{schema}".card_types AS card_types ON card_types.id = payment_methods.card_type_id
        WHERE orders.id = @OrderId
        """

    let getOrAddPaymentMethod (DbSchema schema) =
        $"""
        WITH incoming AS (
            MERGE INTO "%s{schema}".payment_methods AS existing
            USING (VALUES (@BuyerId, @CardTypeId, @CardNumber, @CardHolderName, @CardExpiration))
                    AS pt(buyer_id, card_type_id, card_number, card_holder_name, card_expiration)
                    ON pt.buyer_id = existing.buyer_id
                        AND pt.card_type_id = existing.card_type_id
                        AND pt.card_number = existing.card_number
                        AND pt.card_holder_name = existing.card_holder_name
                        AND pt.card_expiration = existing.card_expiration
                WHEN MATCHED THEN DO NOTHING
                WHEN NOT MATCHED THEN INSERT (buyer_id, card_type_id, card_number, card_holder_name, card_expiration)
                    VALUES (pt.buyer_id, pt.card_type_id, pt.card_number, pt.card_holder_name, pt.card_expiration)
                RETURNING existing.id
            )
        SELECT id FROM incoming
        UNION ALL
        SELECT id FROM "%s{schema}".payment_methods
        WHERE
            buyer_id = @BuyerId
            AND card_type_id = @CardTypeId
            AND card_number = @CardNumber
            AND card_holder_name = @CardHolderName
            AND card_expiration = @CardExpiration
        """

    let upsertBuyer (DbSchema schema) =
        $"""
        INSERT INTO "%s{schema}".buyers (id, name) VALUES (@BuyerId, @BuyerName)
        ON CONFLICT (id)
        DO UPDATE SET name = COALESCE(EXCLUDED.name, @BuyerName)
        """

    let upsertOrderItem (DbSchema schema) =
        $"""
        INSERT INTO "%s{schema}".order_items (product_id, order_id, product_name, unit_price, units, discount, picture_url)
        VALUES (@ProductId, @OrderId, @ProductName, @UnitPrice, @Units, @Discount, @PictureUrl)
        ON CONFLICT (product_id, order_id)
        DO UPDATE SET
            product_name = EXCLUDED.product_name, unit_price = EXCLUDED.unit_price,
            units = EXCLUDED.units, discount = EXCLUDED.discount, picture_url = EXCLUDED.picture_url
        """

    let upsertOrder (DbSchema schema) =
        $"""
        INSERT INTO "%s{schema}".orders (id, buyer_id, payment_method_id, status, description, started_at, street, city, state, country, zip_code)
        VALUES (@Id, @BuyerId, @PaymentMethodId, @Status, @Description, @StartedAt, @Street, @City, @State, @Country, @ZipCode)
        ON CONFLICT (id)
        DO UPDATE SET
            buyer_id = EXCLUDED.buyer_id, payment_method_id = EXCLUDED.payment_method_id,
            status = EXCLUDED.status, description = EXCLUDED.description, started_at = EXCLUDED.started_at, street = EXCLUDED.street,
            city = EXCLUDED.city, state = EXCLUDED.state, country = EXCLUDED.country, zip_code = EXCLUDED.zip_code
        """

type ReadOrderAggregate = OrderAggregateManagementPort.ReadOrderAggregate<SqlIoError>

let readOrderAggregate dbSchema sqlSession : ReadOrderAggregate =
    fun (AggregateId aggregateId) ->
        taskResult {
            let! orderDtos =
                {| OrderId = aggregateId |}
                |> Dapper.query<Dto.Order> sqlSession (Sql.getOrderById dbSchema)
                |> TaskResult.map (Seq.groupBy _.Id)

            return!
                orderDtos
                |> Result.requireSingleOrEmpty
                |> Result.mapError InvalidData
                |> Result.bind (Dto.Order.toDomain >> Result.mapError InvalidData)
        }

type PersistOrderAggregate = OrderAggregateManagementPort.PersistOrderAggregate<SqlIoError>

let persistOrderAggregate dbSchema dbTransaction : PersistOrderAggregate =
    fun (AggregateId aggregateId) order ->
        taskResult {
            let sqlSession = dbTransaction |> SqlSession.Sustained

            do!
                order
                |> Order.getBuyerId
                |> Option.map (fun buyerId ->
                    {| BuyerId = buyerId |> UserId.value
                       BuyerName = order |> Order.getBuyerName |> Option.map BuyerName.value |> Option.toObj |}
                    |> Dapper.execute sqlSession (Sql.upsertBuyer dbSchema)
                    |> TaskResult.ignore)
                |> Option.defaultValue (TaskResult.ok ())

            let! maybePaymentMethodId =
                order
                |> Order.getPaymentMethod
                |> Option.map (fun paymentMethod ->
                    order
                    |> Order.getBuyerId
                    |> Result.requireSome ("Missing BuyerId for existing Payment Method" |> InvalidData)
                    |> TaskResult.ofResult
                    |> TaskResult.bind (fun buyerId ->
                        {| BuyerId = buyerId |> UserId.value
                           CardTypeId = paymentMethod.CardType.Id |> CardTypeId.value
                           CardNumber = paymentMethod.CardNumber |> CardNumber.value
                           CardHolderName = paymentMethod.CardHolderName |> CardHolderName.value
                           CardExpiration = paymentMethod.CardExpiration |}
                        |> Dapper.executeScalar<Guid> sqlSession (Sql.getOrAddPaymentMethod dbSchema)
                        |> TaskResult.map Some))
                |> Option.defaultValue (TaskResult.ok None)

            do!
                order
                |> Dto.OrderStatus.ofOrder
                |> Option.map (fun status ->
                    let maybeAddress = order |> Order.getAddress

                    {| Id = aggregateId
                       BuyerId = order |> Order.getBuyerId |> Option.map UserId.value
                       PaymentMethodId = maybePaymentMethodId
                       Status = status |> Dto.OrderStatus.toString
                       Description = order |> Order.getDescription |> Option.map Description.value
                       StartedAt = order |> Order.getStartedAt
                       Street = maybeAddress |> Option.map (_.Street >> Street.value)
                       City = maybeAddress |> Option.map (_.City >> City.value)
                       State = maybeAddress |> Option.map (_.State >> State.value)
                       Country = maybeAddress |> Option.map (_.Country >> Country.value)
                       ZipCode = maybeAddress |> Option.map (_.ZipCode >> ZipCode.value) |}
                    |> Dapper.execute sqlSession (Sql.upsertOrder dbSchema)
                    |> TaskResult.ignore)
                |> Option.defaultValue (TaskResult.ok ())

            do!
                order
                |> Order.getOrderItems
                |> Map.toList
                |> List.traverseTaskResultM (fun (id, item) ->
                    {| ProductId = id |> ProductId.value
                       OrderId = aggregateId
                       ProductName = item |> OrderItem.getProductName |> ProductName.value
                       UnitPrice = item |> OrderItem.getUnitPrice |> UnitPrice.value
                       Units = item |> OrderItem.getUnits |> Units.value
                       Discount = item |> OrderItem.getDiscount |> Discount.value
                       PictureUrl = item |> OrderItem.getPictureUrl |}
                    |> Dapper.execute sqlSession (Sql.upsertOrderItem dbSchema))
                |> TaskResult.ignore
        }

type PersistOrderAggregateEvents = OrderAggregateManagementPort.PersistOrderAggregateEvents<SqlIoError>

let persistOrderAggregateEvents jsonOptions dbSchema dbTransaction : PersistOrderAggregateEvents =
    Postgres.persistEvents Dto.Event.ofDomain jsonOptions dbSchema dbTransaction

type ReadUnprocessedOrderAggregateEvents = ReadUnprocessedEvents<Order.State, Order.Event, SqlIoError>

let readUnprocessedOrderAggregateEvents jsonOptions dbSchema sqlSession : ReadUnprocessedOrderAggregateEvents =
    Postgres.readUnprocessedEvents Dto.Event.toDomain jsonOptions dbSchema sqlSession
