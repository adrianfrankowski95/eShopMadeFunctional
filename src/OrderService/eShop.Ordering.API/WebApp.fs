module eShop.Ordering.API.WebApp

open Giraffe
open Microsoft.AspNetCore.Http
open eShop.Ordering.API.HttpHandlers

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
                        StartOrderHandler.post CompositionRoot.buildStartOrderWorkflowFromCtx
                    ) ])

          routef "/%s" (fun orderId -> GET >=> text $"Get order by ID: %s{orderId}")

          route "/cardtypes" >=> GET >=> text "Get card types"

          route "/ship" >=> PUT >=> text "Ship order"

          route "/cancel" >=> PUT >=> text "Cancel order"

          route "/draft" >=> POST >=> text "Create draft order" ]

let webApp: (HttpFunc -> HttpContext -> HttpFuncResult) =
    subRoute "/api" (choose [ subRoute "/orders" ordersApi ])
