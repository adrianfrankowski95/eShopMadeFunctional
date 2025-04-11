module eShop.Ordering.API.WebApp

open Giraffe
open Microsoft.AspNetCore.Http
open eShop.Ordering.API.HttpHandlers

let notLoggedIn =
    RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

let webApp: (HttpFunc -> HttpContext -> HttpFuncResult) =
    choose
        [ subRoute "api/orders" (requiresAuthentication notLoggedIn)
          >=> (choose
              [ GET >=> route "/" >=> text "Get orders by user"
                GET >=> routef "/%i" (fun orderId -> text $"Get order by ID: %d{orderId}")
                GET >=> route "/cardtypes" >=> text "Get card types"
                PUT >=> route "/ship" >=> text "Ship order"
                PUT >=> route "/cancel" >=> text "Cancel order"
                POST
                >=> route "/"
                >=> bindJson<StartOrderHandler.Request> (
                    StartOrderHandler.post CompositionRoot.buildStartOrderWorkflowFromCtx
                )
                POST >=> route "/draft" >=> text "Create draft order" ]) ]
