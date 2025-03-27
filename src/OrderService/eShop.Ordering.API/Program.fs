module eShop.Ordering.API.Program

open Microsoft.AspNetCore.Builder

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args) |> Configuration.configureBuilder

    let app = builder.Build() |> Configuration.configureApp

    app.Run()

    0
