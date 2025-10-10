[<RequireQualifiedAccess>]
module eShop.Prelude.Tests.Tuple

open Expecto
open FsCheck
open eShop.Prelude

let tests =
    testList "Tuple Tests" [
        testProperty "mapFst transforms only first element" <| fun (x: int) (y: string) ->
            let tuple = (x, y)
            let mapped = Tuple.mapFst string tuple
            snd mapped = y && fst mapped = string x

        testProperty "mapSnd transforms only second element" <| fun (x: int) (y: string) ->
            let tuple = (x, y)
            let mapped = Tuple.mapSnd int tuple
            fst mapped = x && (try snd mapped = int y with _ -> true)
    ]
