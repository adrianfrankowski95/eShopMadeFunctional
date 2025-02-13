namespace eShop.Prelude

open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module AsyncResultOption =
    let inline defaultValue x = AsyncResult.map (Option.defaultValue x)

    let inline requireSome error =
        AsyncResult.bind (Result.requireSome error >> AsyncResult.ofResult)
