module Ordering.API.WebApp

open Giraffe
open Microsoft.AspNetCore.Http

let webApp: (HttpFunc -> HttpContext -> HttpFuncResult) =
    choose [ route "/ping" >=> text "pong"; route "/" >=> htmlFile "/pages/index.html" ]
