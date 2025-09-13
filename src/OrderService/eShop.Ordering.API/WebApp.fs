module eShop.Ordering.API.WebApp

open Giraffe
open Microsoft.AspNetCore.Http
open eShop.DomainDrivenDesign
open eShop.Ordering.API.GiraffeExtensions
open eShop.Ordering.API.HttpHandlers
open eShop.Ordering.Domain.Model

let private requiresLoggedIn: HttpHandler =
    requiresAuthentication (RequestErrors.UNAUTHORIZED "" "" "You must be logged in.")

let private ordersApi: HttpHandler =
    choose
        [ subRoute
              "/"
              (choose
                  [ GET >=> json []
                    POST
                    >=> bindJson<StartOrderHandler.Request> (
                        StartOrderHandler.post CompositionRoot.OrderWorkflows.buildStartOrderFromCtx
                    ) ])

          routef
              "/%O"
              (validateParam (AggregateId.ofGuid<Order.State> >> Ok) (fun orderId ->
                  GET >=> text $"Get order by ID: %O{orderId.ToString()}"))

          route "/cardtypes" >=> GET >=> text "Get card types"

          route "/ship" >=> PUT >=> text "Ship order"

          route "/cancel" >=> PUT >=> text "Cancel order"

          route "/draft" >=> POST >=> text "Create draft order" ]

let webApp: (HttpFunc -> HttpContext -> HttpFuncResult) =
    subRoute "/api" (choose [ subRoute "/orders" ordersApi ])
