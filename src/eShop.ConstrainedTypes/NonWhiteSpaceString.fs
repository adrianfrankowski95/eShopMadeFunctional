namespace eShop.ConstrainedTypes

open System

type NonWhiteSpaceString = private NonWhiteSpaceString of string

[<RequireQualifiedAccess>]
module NonWhiteSpaceString =
    let create (rawString: string) =
        match rawString |> String.IsNullOrWhiteSpace with
        | true -> rawString |> sprintf "Invalid non-whitespace string: %s" |> Error
        | false -> rawString |> NonWhiteSpaceString |> Ok
    
    let value (NonWhiteSpaceString rawString) = rawString

[<AutoOpen>]
module ActivePatterns =
    let (|NonWhiteSpaceString|) = NonWhiteSpaceString.value
