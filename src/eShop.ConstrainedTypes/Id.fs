namespace eShop.ConstrainedTypes

open System

[<Struct>]
type Id<[<Measure>] 't> private (rawId: Guid) =
    member private this.raw = rawId

    static member ofGuid(guid: Guid) : Id<'t> = guid |> Id<'t>

    static member inline ofString(rawString: string) : Result<Id<'t>, string> =
        match Guid.TryParse rawString with
        | true, guid -> guid |> Id<'t>.ofGuid |> Ok
        | false, _ -> rawString |> sprintf "Invalid Id format: %s" |> Error

    static member value(id: Id<'t>) : Guid = id.raw

    static member toString(id: Id<'t>) = id.raw.ToString()

    static member generate<'t>() : Id<'t> = Guid.NewGuid() |> Id<'t>

type GenerateId<[<Measure>] 't> = unit -> Id<'t>
