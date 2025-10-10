[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Workflows.StartOrderWorkflow

open System
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open FsToolkit.ErrorHandling

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
    | InvalidOrderState of Order.InvalidStateError

type StartOrderWorkflow<'ioErr> = Command -> OrderWorkflow<DomainError, 'ioErr, unit>

[<RequireQualifiedAccess>]
module StartOrderWorkflow =
    let build
        (getCurrentTime: GetUtcNow)
        (getSupportedCardTypes: PaymentManagementPort.GetSupportedCardTypes<'ioErr>)
        (verifyPaymentMethod: PaymentManagementPort.VerifyPaymentMethod<'ioErr>)
        : StartOrderWorkflow<'ioErr> =
        fun command ->
            workflow {
                let now = getCurrentTime ()

                let! supportedCardTypes = (getSupportedCardTypes ()) |> Workflow.usePort

                let! cardType =
                    command.CardTypeId
                    |> CardType.create supportedCardTypes
                    |> Result.mapError (fun (UnsupportedCardTypeError cardTypeId) -> cardTypeId |> UnsupportedCardType)
                    |> AggregateAction.ofResult

                let! unverifiedPaymentMethod =
                    UnverifiedPaymentMethod.create
                        cardType
                        command.CardNumber
                        command.CardSecurityNumber
                        command.CardHolderName
                        command.CardExpiration
                        now
                    |> Result.mapError (fun (_: PaymentMethodExpiredError) -> PaymentMethodExpired)
                    |> AggregateAction.ofResult

                let! verificationResult = unverifiedPaymentMethod |> verifyPaymentMethod |> Workflow.usePort

                let! verifiedPaymentMethod =
                    verificationResult
                    |> PaymentManagementPort.VerificationResult.requireSuccess InvalidPaymentMethod
                    |> AggregateAction.ofResult

                let! validatedOrderItems =
                    command.OrderItems
                    |> NonEmptyMap.traverseResultA (fun (productId, unvalidatedOrderItem) ->
                        unvalidatedOrderItem
                        |> UnvalidatedOrderItem.validate
                        |> Result.map (fun orderItem -> productId, orderItem)
                        |> Result.mapError (fun err -> productId, err))
                    |> Result.mapError (Map.ofList >> InvalidOrderItems)
                    |> AggregateAction.ofResult

                let createOrderCommand: Order.Command.CreateOrder =
                    { Buyer = command.Buyer
                      Address = command.Address
                      PaymentMethod = verifiedPaymentMethod
                      OrderItems = validatedOrderItems
                      OrderedAt = now }

                do! createOrderCommand |> Order.create |> AggregateAction.mapError InvalidOrderState
            }
