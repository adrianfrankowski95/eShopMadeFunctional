module eShop.Ordering.API.GiraffeExtensions

open eShop.Prelude
open Giraffe

[<RequireQualifiedAccess>]
module HttpFuncResult =
    let inline ofAsyncResult next ctx x : HttpFuncResult =
        task {
            let! httpHandler = x |> AsyncResult.collapse

            return! httpHandler next ctx
        }
