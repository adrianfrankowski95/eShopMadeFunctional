[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderQueries

open System
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.Postgres
open eShop.Prelude
open FsToolkit.ErrorHandling

module internal Dto =
    [<CLIMutable>]
    type Order =
        { OrderNumber: Guid
          Date: DateTimeOffset option
          Status: string
          Description: string option
          Street: string option
          City: string option
          State: string option
          ZipCode: string option
          Country: string option
          ProductName: string option
          Unit: int option
          UnitPrice: decimal option
          PictureUrl: string option }

    [<CLIMutable>]
    type OrderSummary =
        { UserId: Guid
          OrderNumber: Guid
          Date: DateTimeOffset option
          Status: string
          Description: string option
          Unit: int option
          UnitPrice: decimal option }

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

    let getUserSummaries (DbSchema schema) =
        $"""
        SELECT buyers.id as "UserId", orders.id as "OrderNumber", orders.started_at as "Date", orders.status as "Status"
        items.units as "Unit", items.unit_price as "UnitPrice"
        FROM "%s{schema}".orders AS orders
        LEFT JOIN "%s{schema}".buyers AS buyers ON orders.buyer_id = buyers.id
        LEFT JOIN "%s{schema}".order_items AS items ON items.order_id = orders.id
        WHERE buyers.id = @UserId
        """

    let getCardTypes (DbSchema schema) =
        $"""SELECT id as "Id", name as "Name" FROM "%s{schema}".card_types"""

let getByIdQuery dbSchema sqlSession : OrderQueries.GetById<SqlIoError> =
    fun (AggregateId aggregateId) ->
        asyncResult {
            let! orders =
                {| OrderId = aggregateId |}
                |> Dapper.query<Dto.Order> sqlSession (Sql.getOrderById dbSchema)
                |> AsyncResult.map (Seq.groupBy _.OrderNumber)

            return!
                orders
                |> Result.requireSingleOrEmpty
                |> Result.mapError InvalidData
                |> Result.map (
                    Option.map (fun (_, dtos) ->
                        let dto = dtos |> Seq.head

                        let orderItems: OrderQueries.OrderItem list =
                            dtos
                            |> List.ofSeq
                            |> List.map (fun dto ->
                                { PictureUrl = dto.PictureUrl |> Option.toObj
                                  ProductName = dto.ProductName |> Option.toObj
                                  Unit = dto.Unit |> Option.defaultValue 0
                                  UnitPrice = dto.UnitPrice |> Option.map double |> Option.defaultValue 0 })

                        let total =
                            dtos
                            |> Seq.sumBy (fun dto ->
                                let unit = dto.Unit |> Option.map decimal |> Option.defaultValue 0m
                                let price = dto.UnitPrice |> Option.defaultValue 0m

                                unit * price)

                        ({ OrderNumber = dto.OrderNumber
                           Status = dto.Status
                           Date = dto.Date |> Option.map _.DateTime |> Option.defaultValue DateTime.MinValue
                           Description = dto.Description |> Option.toObj
                           OrderItems = orderItems
                           City = dto.City |> Option.toObj
                           Country = dto.Country |> Option.toObj
                           State = dto.State |> Option.toObj
                           Street = dto.Street |> Option.toObj
                           Zipcode = dto.ZipCode |> Option.toObj
                           Total = total }
                        : OrderQueries.Order))
                )
        }

let getUserSummariesQuery dbSchema sqlSession : OrderQueries.GetUserSummaries<SqlIoError> =
    fun userId ->
        {| UserId = userId |> UserId.value |}
        |> Dapper.query<Dto.OrderSummary> sqlSession (Sql.getUserSummaries dbSchema)
        |> AsyncResult.map (
            Seq.groupBy _.OrderNumber
            >> Seq.map (fun (orderId, dtos) ->
                let dto = dtos |> Seq.head

                let total =
                    dtos
                    |> Seq.sumBy (fun dto ->
                        let unit = dto.Unit |> Option.map decimal |> Option.defaultValue 0m
                        let price = dto.UnitPrice |> Option.defaultValue 0m

                        unit * price)

                { OrderNumber = orderId
                  Date = dto.Date |> Option.map _.DateTime |> Option.defaultValue DateTime.MinValue
                  Status = dto.Status
                  Total = total |> double }
                : OrderQueries.OrderSummary)
        )

let getCardTypesQuery dbSchema sqlSession : OrderQueries.GetCardTypes<SqlIoError> =
    fun () -> Dapper.query<OrderQueries.CardType> sqlSession (Sql.getCardTypes dbSchema) null
