module Ordering.API.Program

open Microsoft.AspNetCore.Builder

[<EntryPoint>]
let main _ =
    let builder = WebApplication.CreateBuilder() |> Configuration.configureBuilder
    
    let app = builder.Build() |> Configuration.configureApp
    
    app.Run()

    0
