[<RequireQualifiedAccess>]
module Ordering.Adapters.Postgres.PostgresOrderManagementAdapter

open System
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.Ordering
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Ports
open eShop.DomainDrivenDesign
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
          PaymentMethodExpiration: DateTimeOffset option
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
                       Expiration = expiration }
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

        let toDomain (maybeOrder: (int * Order seq) option) : Result<Domain.Model.Order, string> =
            maybeOrder
            |> Option.map (fun (_, orders) ->
                validation {
                    let orderDto = orders |> Seq.head
                    let buyerId = orderDto.BuyerId |> BuyerId.ofGuid

                    let! status = orderDto.Status |> OrderStatus.create

                    return!
                        match status with
                        | Draft ->
                            orders
                            |> Seq.traverseResultA createUnvalidatedOrderItem
                            |> Result.map (
                                Seq.choose id
                                >> Map.ofSeq
                                >> fun items ->
                                    ({ BuyerId = buyerId
                                       UnvalidatedOrderItems = items }
                                    : Order.Draft)
                                    |> Order.Draft
                            )
                            |> Result.mapError List.ofSeq
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
                                    : Order.AwaitingStockValidation)
                                    |> Order.AwaitingStockValidation
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
                                    : Order.WithConfirmedStock)
                                    |> Order.WithConfirmedStock
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
                                    : Order.Paid)
                                    |> Order.Paid
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
                                    : Order.Shipped)
                                    |> Order.Shipped
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
                                    : Order.Cancelled)
                                    |> Order.Cancelled
                            }
                }
                |> Result.mapError (String.concat "; "))
            |> Option.defaultValue (Order.Init |> Ok)

module private Sql =
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

let private requireSingleOrEmpty x =
    x
    |> Result.requireSingleOrEmpty
    |> Result.mapError InvalidData
    |> AsyncResult.ofResult

type ReadOrderAggregate = OrderManagementPort.ReadOrderAggregate<SqlIoError>

let readOrderAggregate dbSchema sqlSession : ReadOrderAggregate =
    fun aggregateId ->
        {| OrderId = aggregateId |> AggregateId.value |}
        |> Dapper.query<Dto.Order> sqlSession (Sql.getOrderById dbSchema)
        |> AsyncResult.map (Seq.groupBy _.Id)
        |> AsyncResult.bind requireSingleOrEmpty
        |> Async.map (Result.bind (Dto.Order.toDomain >> Result.mapError InvalidData))

type PersistOrderAggregate = OrderManagementPort.PersistOrderAggregate<SqlIoError>

let persistOrderAggregate dbSchema sqlSession: PersistOrderAggregate =
    fun aggregateId state ->
        asyncResult {
            return!
                match state with
                | Order.Init -> AsyncResult.ok()
                | Order.Draft draft -> failwith "todo"
                | Order.AwaitingStockValidation awaitingStockValidation -> failwith "todo"
                | Order.WithConfirmedStock withConfirmedStock -> failwith "todo"
                | Order.Paid paid -> failwith "todo"
                | Order.Shipped shipped -> failwith "todo"
                | Order.Cancelled cancelled -> failwith "todo"
        }


