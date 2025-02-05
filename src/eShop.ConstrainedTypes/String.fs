[<RequireQualifiedAccess>]
module eShop.ConstrainedTypes.String

open System
open eShop.Prelude.Operators
open FsToolkit.ErrorHandling

module Constraints =
    let nonNull ctor fieldName rawString =
        rawString
        |> isNull
        |> Result.requireFalse $"%s{fieldName} is invalid: value is null"
        |> Result.map (fun _ -> rawString |> ctor)

    let nonWhiteSpace ctor fieldName rawString =
        rawString
        |> String.IsNullOrWhiteSpace
        |> Result.requireFalse $"%s{fieldName} is invalid: Expected non-whitespace value, actual value: %s{rawString}"
        |> Result.map (fun _ -> rawString |> ctor)

    let private lengthConstraint comparison length ctor fieldName error =
        let lengthConstraint rawString =
            rawString
            |> String.length
            |> comparison length
            |> Result.requireTrue error
            |> Result.map (fun _ -> rawString |> ctor)

        nonNull id fieldName >=> lengthConstraint

    let minLength minLength ctor fieldName rawString =
        lengthConstraint
            (<=)
            minLength
            ctor
            fieldName
            $"%s{fieldName} is invalid: Expected min length: %d{minLength}, actual value: %s{rawString}"
            rawString

    let maxLength maxLength ctor fieldName rawString =
        lengthConstraint
            (>=)
            maxLength
            ctor
            fieldName
            $"%s{fieldName} is invalid: Expected max length: %d{maxLength}, actual value: %s{rawString}"
            rawString

    let fixedLength length ctor fieldName rawString =
        lengthConstraint
            (=)
            length
            ctor
            fieldName
            $"%s{fieldName} is invalid: Expected length: %d{length}, actual value: %s{rawString}"
            rawString

    module Operators =
        let (>=>)
            (f: (string -> _) -> string -> string -> Result<string, string>)
            (g: (string -> 'a) -> string -> string -> Result<'a, string>)
            =
            fun ctor fieldName -> f id fieldName >=> (g ctor fieldName)


type NonWhiteSpace = private NonWhiteSpace of string

module NonWhiteSpace =
    let create = Constraints.nonWhiteSpace NonWhiteSpace
