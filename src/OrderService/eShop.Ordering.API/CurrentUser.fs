namespace eShop.Ordering.API

open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open eShop.ConstrainedTypes
open FsToolkit.ErrorHandling
open eShop.Ordering.Domain.Model
open eShop.ServiceDefaults

type private Logger = Logger


type UserName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module UserName =
    let create = String.NonWhiteSpace.create (nameof UserName)


type CurrentUser = { Id: UserId; Name: UserName }

[<RequireQualifiedAccess>]
module CurrentUser =
    let create (ctx: HttpContext) =
        let logError err =
            ctx
                .GetLogger<Logger>()
                .LogError("An error occurred when processing StartOrder request: {Error}", [| err |])

        validation {
            do!
                ctx.User.Identity.IsAuthenticated
                |> Result.requireTrue "Cannot retrieve Current User: User not authenticated"

            let! userId =
                ctx.User.GetUserId()
                |> UserId.ofString
                |> Result.mapError ((+) "Cannot retrieve Current User ID: ")

            and! userName =
                ctx.User.GetUserName()
                |> UserName.create
                |> Result.mapError ((+) "Cannot retrieve Current User Name: ")

            return { Id = userId; Name = userName }
        }
        |> Result.mapError (
            String.concat "; "
            >> fun err ->
                err |> logError
                err |> RequestErrors.UNAUTHORIZED "" ""
        )

    let asBuyer (user: CurrentUser) : Buyer =
        { Id = user.Id
          Name = user.Name }
