[<RequireQualifiedAccess>]
module Ordering.API.Configuration

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open eShop.ServiceDefaults
open eShop.EventBusRabbitMQ
open Ordering.API.WebApp

let private configureServices (services: IServiceCollection) =
    services.AddProblemDetails().AddGiraffe() |> ignore

let configureBuilder (builder: WebApplicationBuilder) =
    builder.AddServiceDefaults().AddDefaultAuthentication() |> configureServices
    builder.AddRabbitMqEventBus("eventbus") |> ignore
    builder

let configureApp (app: WebApplication) =
    app.MapDefaultEndpoints().UseAuthorization() |> ignore
    app.UseGiraffe webApp

    app
