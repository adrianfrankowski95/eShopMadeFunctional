namespace Ordering.Domain.Workflows

open System
open Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open Ordering.Domain.Model
open Ordering.Domain.Ports
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module StartOrderWorkflow =
    type NewOrExistingBuyer =
        | New of BuyerName
        | Existing of BuyerId

    type Command =
        { Buyer: NewOrExistingBuyer
          Address: Address
          OrderItems: NonEmptyMap<ProductId, UnvalidatedOrderItem>
          CardTypeId: CardTypeId
          CardNumber: CardNumber
          CardSecurityNumber: CardSecurityNumber
          CardHolderName: CardHolderName
          Expiration: DateTimeOffset }

    type DomainError =
        | BuyerNotFound of BuyerId
        | UnsupportedCardType of CardTypeId
        | PaymentMethodExpired
        | InvalidPaymentMethod of UnverifiedPaymentMethod
        | InvalidOrderItems of Map<ProductId, DiscountHigherThanTotalPriceError>
        | InvalidOrderState of InvalidOrderStateError

    type T<'ioError> = Workflow<Command, Order, DomainEvent, DomainError, 'ioError>

    let build
        (generateBuyerId: GenerateId<buyerId>)
        (getBuyer: BuyerManagementPort.GetBuyer<'ioError>)
        (getSupportedCardTypes: BuyerManagementPort.GetSupportedCardTypes<'ioError>)
        (verifyPaymentMethod: BuyerManagementPort.VerifyPaymentMethod<'ioError>)
        : T<'ioError> =
        fun now state command ->
            asyncResult {
                let inline mapToIoError x = x |> AsyncResult.mapError Right

                let! buyer =
                    match command.Buyer with
                    | New buyerName -> generateBuyerId () |> Buyer.create buyerName |> AsyncResult.ok
                    | Existing buyerId ->
                        buyerId
                        |> getBuyer
                        |> mapToIoError
                        |> AsyncResultOption.requireSome (buyerId |> BuyerNotFound |> Left)

                and! supportedCardTypes = getSupportedCardTypes () |> mapToIoError

                let! cardType =
                    command.CardTypeId
                    |> CardType.create supportedCardTypes
                    |> Result.mapError (fun (UnsupportedCardTypeError cardTypeId) ->
                        cardTypeId |> UnsupportedCardType |> Left)

                let! unverifiedPaymentMethod =
                    UnverifiedPaymentMethod.create
                        cardType
                        command.CardNumber
                        command.CardSecurityNumber
                        command.CardHolderName
                        command.Expiration
                        now
                    |> Result.mapError (fun (_: PaymentMethodExpiredError) -> PaymentMethodExpired |> Left)

                let! verifiedPaymentMethod =
                    unverifiedPaymentMethod
                    |> verifyPaymentMethod
                    |> AsyncResult.mapError (function
                        | Right ioError -> ioError |> Right
                        | Left(_: BuyerManagementPort.InvalidPaymentMethodError) ->
                            unverifiedPaymentMethod |> InvalidPaymentMethod |> Left)

                and! validatedOrderItems =
                    command.OrderItems
                    |> NonEmptyMap.traverseResultA (fun (productId, unvalidatedOrderItem) ->
                        unvalidatedOrderItem
                        |> UnvalidatedOrderItem.validate
                        |> Result.map (fun orderItem -> productId, orderItem)
                        |> Result.mapError (fun err -> productId, err))
                    |> Result.mapError (Map.ofList >> InvalidOrderItems >> Left)

                let createOrderCommand: Command.CreateOrder =
                    { Buyer = buyer
                      Address = command.Address
                      VerifiedPaymentMethod = verifiedPaymentMethod
                      OrderItems = validatedOrderItems
                      OrderDate = now }

                return!
                    createOrderCommand
                    |> Command.CreateOrder
                    |> Order.evolve state
                    |> Result.mapError (InvalidOrderState >> Left)
            }

type StartOrderWorkflow<'ioError> = StartOrderWorkflow.T<'ioError>
