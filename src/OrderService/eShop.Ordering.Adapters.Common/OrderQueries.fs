[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Common.OrderQueries

open System
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling

[<CLIMutable>]
type OrderItem =
    { ProductName: string
      Unit: int
      UnitPrice: double
      PictureUrl: string }

[<CLIMutable>]
type Order =
    { OrderNumber: Guid
      Date: DateTime
      Status: string
      Description: string
      Street: string
      City: string
      State: string
      Zipcode: string
      Country: string
      OrderItems: OrderItem list
      Total: decimal }

[<CLIMutable>]
type OrderSummary =
    { OrderNumber: Guid
      Date: DateTime
      Status: string
      Total: double }

[<CLIMutable>]
type CardType = { Id: int; Name: string }

type GetById<'ioError> = Order.Id -> TaskResult<Order option, 'ioError>

type GetUserSummaries<'ioError> = UserId -> TaskResult<OrderSummary seq, 'ioError>

type GetCardTypes<'ioError> = unit -> TaskResult<CardType seq, 'ioError>
