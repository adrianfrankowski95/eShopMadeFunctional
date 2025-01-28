namespace eShop.Prelude

open FsToolkit.ErrorHandling

type AsyncResult<'ok, 'err> = Result<'ok, 'err> Async

[<RequireQualifiedAccess>]
module AsyncResult =
    let collapse x = x |> Async.map Result.collapse
