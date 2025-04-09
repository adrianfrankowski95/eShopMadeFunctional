namespace eShop.Ordering.Domain.Workflows

open System
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module StartOrderWorkflow =
    type Command =
        { Buyer: Buyer
          Address: Address
          OrderItems: NonEmptyMap<ProductId, UnvalidatedOrderItem>
          CardTypeId: CardTypeId
          CardNumber: CardNumber
          CardSecurityNumber: CardSecurityNumber
          CardHolderName: CardHolderName
          CardExpiration: DateOnly }

    type DomainError =
        | UnsupportedCardType of CardTypeId
        | PaymentMethodExpired
        | InvalidPaymentMethod of UnverifiedPaymentMethod
        | InvalidOrderItems of Map<ProductId, DiscountHigherThanTotalPriceError>
        | InvalidOrderState of OrderAggregate.InvalidStateError

    type T<'ioError> = ExecutableWorkflow<Command, OrderAggregate.State, OrderAggregate.Event, DomainError, 'ioError>

    let build
        (getSupportedCardTypes: PaymentManagementPort.GetSupportedCardTypes<'ioError>)
        (verifyPaymentMethod: PaymentManagementPort.VerifyPaymentMethod<'ioError>)
        : T<'ioError> =
        fun now state command ->
            asyncResult {
                let inline mapToIoError x = x |> AsyncResult.mapError Right

                let! supportedCardTypes = getSupportedCardTypes () |> mapToIoError

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
                        command.CardExpiration
                        now
                    |> Result.mapError (fun (_: PaymentMethodExpiredError) -> PaymentMethodExpired |> Left)

                let! verifiedPaymentMethod =
                    unverifiedPaymentMethod
                    |> verifyPaymentMethod
                    |> AsyncResult.mapError (function
                        | Right ioError -> ioError |> Right
                        | Left(_: PaymentManagementPort.InvalidPaymentMethodError) ->
                            unverifiedPaymentMethod |> InvalidPaymentMethod |> Left)

                and! validatedOrderItems =
                    command.OrderItems
                    |> NonEmptyMap.traverseResultA (fun (productId, unvalidatedOrderItem) ->
                        unvalidatedOrderItem
                        |> UnvalidatedOrderItem.validate
                        |> Result.map (fun orderItem -> productId, orderItem)
                        |> Result.mapError (fun err -> productId, err))
                    |> Result.mapError (Map.ofList >> InvalidOrderItems >> Left)

                let createOrderCommand: OrderAggregate.Command.CreateOrder =
                    { Buyer = command.Buyer
                      Address = command.Address
                      PaymentMethod = verifiedPaymentMethod
                      OrderItems = validatedOrderItems
                      OrderedAt = now }

                return!
                    createOrderCommand
                    |> OrderAggregate.Command.CreateOrder
                    |> OrderAggregate.evolve state
                    |> Result.mapError (InvalidOrderState >> Left)
            }

type StartOrderWorkflow<'ioError> = StartOrderWorkflow.T<'ioError>
