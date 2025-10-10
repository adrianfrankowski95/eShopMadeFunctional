[<RequireQualifiedAccess>]
module eShop.Ordering.API.HttpHandlers.StartOrderHandler

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open eShop.ConstrainedTypes
open eShop.Ordering.API
open eShop.Ordering.API.GiraffeExtensions
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Workflows
open FsToolkit.ErrorHandling

type Logger = Logger

[<CLIMutable>]
type OrderItemDto =
    { ProductId: int
      ProductName: string
      UnitPrice: decimal
      Discount: decimal
      Quantity: int
      PictureUrl: string }

[<CLIMutable>]
type Request =
    { City: string
      State: string
      Country: string
      ZipCode: string
      Street: string
      CardNumber: string
      CardHolderName: string
      CardExpiration: DateTime
      CardSecurityNumber: string
      CardTypeId: int
      Items: OrderItemDto list }

[<RequireQualifiedAccess>]
module Request =
    let toWorkflowCommand (request: Request) (currentUser: CurrentUser) : Result<StartOrderWorkflow.Command, _> =
        validation {
            let! city = request.City |> City.create
            and! state = request.State |> State.create
            and! country = request.Country |> Country.create
            and! zipCode = request.ZipCode |> ZipCode.create
            and! street = request.Street |> Street.create
            //and! cardNumber = request.CardNumber |> String. |> CardNumber.create
            and! cardNumber = request.CardNumber[..7] + "XXXX" |> CardNumber.create
            and! cardHolderName = request.CardHolderName |> CardHolderName.create
            and! cardSecurityNumber = request.CardSecurityNumber |> CardSecurityNumber.create

            and! orderItems =
                request.Items
                |> List.traverseResultA (fun item ->
                    validation {
                        let productId = item.ProductId |> ProductId.ofInt
                        let pictureUrl = item.PictureUrl |> Option.ofObj

                        let! productName = item.ProductName |> ProductName.create
                        and! unitPrice = item.UnitPrice |> UnitPrice.create
                        and! discount = item.Discount |> Discount.create
                        and! units = item.Quantity |> Units.create

                        let unvalidatedOrderItem: UnvalidatedOrderItem =
                            { Discount = discount
                              Units = units
                              PictureUrl = pictureUrl
                              ProductName = productName
                              UnitPrice = unitPrice }

                        return productId, unvalidatedOrderItem
                    }
                    |> Result.mapError (String.concat "; "))
                |> Result.mapError (String.concat "; ")
                |> Result.bind (NonEmptyMap.ofSeq >> Result.mapError ((+) "Invalid OrderItems: "))

            return
                ({ Buyer = currentUser |> CurrentUser.asBuyer
                   Address =
                     { City = city
                       State = state
                       Country = country
                       ZipCode = zipCode
                       Street = street }
                   CardTypeId = request.CardTypeId |> CardTypeId.ofInt
                   CardExpiration = DateOnly.FromDateTime(request.CardExpiration)
                   CardNumber = cardNumber
                   OrderItems = orderItems
                   CardHolderName = cardHolderName
                   CardSecurityNumber = cardSecurityNumber }
                : StartOrderWorkflow.Command)
        }
        |> Result.mapError (String.concat "; ")

let post
    (buildStartOrderWorkflow: HttpContext -> StartOrderWorkflow.Command -> TaskResult<_, _>)
    (request: Request)
    : HttpHandler =
    fun next ctx ->
        taskResult {
            let logError err =
                ctx
                    .GetLogger<Logger>()
                    .LogError("An error occurred while processing StartOrder request: {Error}", [| err |])

            let! workflowCommand =
                CurrentUser.create ctx
                |> Result.bind (
                    Request.toWorkflowCommand request
                    >> Result.teeError logError
                    >> Result.mapError (_.ToString() >> RequestErrors.BAD_REQUEST)
                )

            return!
                workflowCommand
                |> buildStartOrderWorkflow ctx
                |> TaskResult.map Successful.OK
                |> TaskResult.teeError logError
                |> TaskResult.mapError (_.ToString() >> ServerErrors.INTERNAL_ERROR)
        }
        |> HttpFuncResult.ofTaskResult next ctx
