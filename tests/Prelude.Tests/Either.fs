[<RequireQualifiedAccess>]
module eShop.Prelude.Tests.Either

open Expecto
open FsCheck
open eShop.Prelude

[<Tests>]
let tests =
    testList "Either Tests" [
        testProperty "mapLeft does not affect Right value" <| fun x ->
            let either = Either.Right x
            let mapped = Either.mapLeft string either
            mapped = Either.Right x

        testProperty "mapRight does not affect Left value" <| fun x ->
            let either = Either.Left x
            let mapped = Either.mapRight string either
            mapped = Either.Left x

        testProperty "bimap applies correct function" <| fun x ->
            let either = if x % 2 = 0 then Either.Right x else Either.Left x
            let mapped = Either.bimap string string either
            match either, mapped with
            | Either.Left l, Either.Left l' -> l.ToString() = l'
            | Either.Right r, Either.Right r' -> r.ToString() = r'
            | _ -> false
    ]
