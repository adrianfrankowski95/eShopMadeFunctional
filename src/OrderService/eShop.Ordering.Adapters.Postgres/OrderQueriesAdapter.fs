[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderQueriesAdapter

open System
open FsToolkit.ErrorHandling
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Ports
open eShop.Postgres

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

module private Sql =
    let getOrderById (DbSchema schema) =
        $"""
        SELECT
            orders.id as "OrderNumber", orders.started_at as "Date", orders.status as "Status",
            orders.description as "Description", orders.street as "Street", orders.city as "City",
            orders.state as "State", orders.zip_code as "ZipCode", orders.country as "Country",
            items.product_name as "ProductName", items.units as "Unit", 
            items.unit_price as "UnitPrice", items.picture_url as "PictureUrl"
        FROM "%s{schema}".orders AS orders
        LEFT JOIN "%s{schema}".order_items AS items ON items.order_id = orders.id
        WHERE orders.id = @OrderId
        """