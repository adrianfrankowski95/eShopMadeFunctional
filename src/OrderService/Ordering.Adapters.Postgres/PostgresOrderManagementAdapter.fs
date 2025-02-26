[<RequireQualifiedAccess>]
module Ordering.Adapters.Postgres.PostgresOrderManagementAdapter

open System
open FsToolkit.ErrorHandling
open Ordering.Domain.Ports
open eShop.DomainDrivenDesign.Postgres

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
            |> Result.map (fun _ -> statusMap |> Map.find)

        let toString status =
            statusMap |> Map.findKey (fun _ v -> v = status)

    [<CLIMutable>]
    type OrderItem =
        { ProductId: int
          OrderId: int
          ProductName: string
          UnitPrice: decimal
          Units: int
          Discount: decimal option
          PictureUrl: string option }

    [<CLIMutable>]
    type Order =
        { Id: int
          Status: string
          OrderItems: OrderItem list
          BuyerId: Guid
          BuyerName: string option
          CardTypeId: int option
          CardTypeName: string option
          CardNumber: string option
          CardHolderName: string option
          PaymentMethodExpiration: DateTimeOffset option
          Street: string option
          City: string option
          State: string option
          Country: string option
          ZipCode: string option
          OrderedAt: DateTimeOffset option }

module private Sql =
    let getOrder = ""

type ReadOrderAggregate = OrderManagementPort.ReadOrderAggregate<SqlIoError> 

let readOrderAggregate: ReadOrderAggregate = failwith ""
