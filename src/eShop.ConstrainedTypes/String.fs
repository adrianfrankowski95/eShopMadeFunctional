[<RequireQualifiedAccess>]
module eShop.ConstrainedTypes.String

open System
open System.Text
open eShop.Prelude.Operators
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
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

    let hasMaxUtf8Bytes maxByteCount ctor fieldName =
        let hasMaxUtf8Bytes (rawString: string) =
            rawString
            |> Encoding.UTF8.GetByteCount
            |> ((>=) maxByteCount)
            |> Result.requireTrue $"%s{fieldName} is invalid: UTF8 byte count %d{maxByteCount} exceeded"
            |> Result.map (fun _ -> rawString |> ctor)

        nonNull id fieldName >=> hasMaxUtf8Bytes

    let doesntStartWith phrase ctor fieldName (rawString: string) =
        rawString[0 .. (phrase |> String.length |> (+) 1)] = phrase
        |> Result.requireFalse $"%s{fieldName} is invalid: It must not start with %s{phrase}"
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

    let inline evaluateM ctor fieldName constraints =
        fun rawString ->
            constraints
            |> Seq.traverseResultM (fun c -> c id fieldName rawString)
            |> Result.map (fun _ -> rawString |> ctor)

    let inline evaluateA ctor fieldName constraints =
        fun rawString ->
            constraints
            |> Seq.traverseResultA (fun c -> c id fieldName rawString)
            |> Result.map (fun _ -> rawString |> ctor)

type NonWhiteSpace = private NonWhiteSpace of string

[<RequireQualifiedAccess>]
module NonWhiteSpace =
    let create = Constraints.nonWhiteSpace NonWhiteSpace

    let value (NonWhiteSpace value) = value