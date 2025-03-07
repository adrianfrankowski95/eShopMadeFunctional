[<RequireQualifiedAccess>]
module eShop.Ordering.API.HttpHandlers.StartOrderHandler

open System
open Giraffe
open Giraffe.HttpStatusCodeHandlers.ServerErrors
open Microsoft.AspNetCore.Http
open eShop.ConstrainedTypes
open eShop.Ordering.API
open eShop.Ordering.API.GiraffeExtensions
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Workflows
open FsToolkit.ErrorHandling
open eShop.Prelude

[<CLIMutable>]
type OrderItemDto =
    { ProductId: int
      ProductName: string
      UnitPrice: decimal
      Discount: decimal
      Units: int
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
      CardExpiration: DateOnly
      CardSecurityNumber: string
      CardTypeId: int
      OrderItems: OrderItemDto list }

[<RequireQualifiedAccess>]
module Request =
    let toWorkflowCommand
        (request: Request)
        (currentUser: CurrentUser)
        : Result<StartOrderWorkflow.Command, HttpHandler> =
        validation {
            let! city = request.City |> City.create
            and! state = request.State |> State.create
            and! country = request.Country |> Country.create
            and! zipCode = request.ZipCode |> ZipCode.create
            and! street = request.Street |> Street.create
            and! cardNumber = request.CardNumber |> CardNumber.create
            and! cardHolderName = request.CardHolderName |> CardHolderName.create
            and! cardSecurityNumber = request.CardSecurityNumber |> CardSecurityNumber.create

            and! orderItems =
                request.OrderItems
                |> List.traverseResultA (fun item ->
                    validation {
                        let productId = item.ProductId |> ProductId.ofInt
                        let pictureUrl = item.PictureUrl |> Option.ofObj

                        let! productName = item.ProductName |> ProductName.create
                        and! unitPrice = item.UnitPrice |> UnitPrice.create
                        and! discount = item.Discount |> Discount.create
                        and! units = item.Units |> Units.create

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
                   CardExpiration = request.CardExpiration
                   CardNumber = cardNumber
                   OrderItems = orderItems
                   CardHolderName = cardHolderName
                   CardSecurityNumber = cardSecurityNumber }
                : StartOrderWorkflow.Command)
        }
        |> Result.mapError (String.concat "; " >> RequestErrors.BAD_REQUEST)

let post
    (buildStartOrderWorkflow: HttpContext -> StartOrderWorkflow.Command -> AsyncResult<unit, _>)
    (request: Request)
    : HttpHandler =
    fun next ctx ->
        CurrentUser.create ctx
        |> Result.bind (Request.toWorkflowCommand request)
        |> AsyncResult.ofResult
        |> AsyncResult.bind (buildStartOrderWorkflow ctx)
        |> AsyncResult.map Successful.OK
        |> AsyncResult.mapError internalError
        |> HttpFuncResult.ofAsyncResult next ctx
