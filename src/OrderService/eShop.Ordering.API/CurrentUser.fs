namespace eShop.Ordering.API

open Giraffe
open Microsoft.AspNetCore.Http
open eShop.ConstrainedTypes
open FsToolkit.ErrorHandling
open eShop.Ordering.Domain.Model
open eShop.ServiceDefaults

[<Measure>]
type userId

type UserId = Id<userId>


type UserName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module UserName =
    let create = String.NonWhiteSpace.create (nameof UserName)


type CurrentUser = { Id: UserId; Name: UserName }

[<RequireQualifiedAccess>]
module CurrentUser =
    let create (ctx: HttpContext) =
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
        |> Result.mapError (String.concat "; " >> RequestErrors.UNAUTHORIZED "" "")

    let asBuyer (user: CurrentUser) : Buyer =
        { Id = user.Id |> Id.value |> BuyerId.ofGuid
          Name = user.Name }
