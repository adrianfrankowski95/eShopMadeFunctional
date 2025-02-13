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
          SecurityNumber: CardSecurityNumber
          CardHolderName: CardHolderName
          Expiration: DateTimeOffset }

    type DomainError =
        | BuyerNotFound of BuyerId
        | InvalidCardType of CardTypeId
        | InvalidPaymentMethod of UnverifiedPaymentMethod
        | InvalidOrderItems of Map<ProductId, DiscountHigherThanTotalPrice>
        | OrderError of OrderError

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
                    |> Result.setError (command.CardTypeId |> InvalidCardType |> Left)

                let unverifiedPaymentMethod =
                    UnverifiedPaymentMethod.create
                        cardType
                        command.CardNumber
                        command.SecurityNumber
                        command.CardHolderName
                        command.Expiration

                let! verifiedPaymentMethod =
                    unverifiedPaymentMethod
                    |> verifyPaymentMethod
                    |> AsyncResult.mapError (function
                        | Left ioError -> ioError |> Right
                        | Right invalidPaymentMethod -> invalidPaymentMethod |> InvalidPaymentMethod |> Left)

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
                    |> Result.mapError (OrderError >> Left)
            }

type StartOrderWorkflow<'ioError> = StartOrderWorkflow.T<'ioError>
