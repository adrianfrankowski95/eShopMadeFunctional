module eShop.Ordering.API.GiraffeExtensions

open System
open Microsoft.Extensions.Logging
open eShop.Prelude
open Giraffe

[<RequireQualifiedAccess>]
module HttpFuncResult =
    let inline ofAsyncResult next ctx x : HttpFuncResult =
        task {
            let! httpHandler = x |> AsyncResult.collapse

            return! httpHandler next ctx
        }

let errorHandler (ex: Exception) (logger: ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> ServerErrors.INTERNAL_ERROR ex.Message

let validateParam (validator: 'param -> Result<'validated, string>) (handler: 'validated -> HttpHandler) =
    validator
    >> Result.mapError RequestErrors.BAD_REQUEST
    >> Result.map handler
    >> Result.collapse
