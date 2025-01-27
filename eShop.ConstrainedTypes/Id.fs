namespace eShop.ConstrainedTypes

open System
open FSharp.UMX

type Id<[<Measure>] 't> = private Id of Guid<'t>

type GenerateId<[<Measure>] 't> = unit -> Id<'t>

[<RequireQualifiedAccess>]
module Id =
    let ofGuid = Id
    
    let create<[<Measure>] 't> (rawString: string): Result<Id<'t>, string> =
        match Guid.TryParse rawString with
        | true, guid -> % guid |> ofGuid |> Ok
        | false, _ -> rawString |> sprintf "Invalid Id format: %s" |> Error
        
    let value (Id rawId) = rawId
    
    let toString (Id rawId) = rawId.ToString()
    
    let generate<[<Measure>] 't>: GenerateId<'t> = fun () -> % Guid.NewGuid() |> ofGuid