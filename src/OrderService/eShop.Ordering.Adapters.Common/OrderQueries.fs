[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Common.OrderQueries

open System
open eShop.Ordering.Domain.Model
open eShop.Prelude

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

type GetById<'ioError> = Order.Id -> AsyncResult<Order option, 'ioError>

type GetUserSummaries<'ioError> = UserId -> AsyncResult<OrderSummary seq, 'ioError>

type GetCardTypes<'ioError> = unit -> AsyncResult<CardType seq, 'ioError>
