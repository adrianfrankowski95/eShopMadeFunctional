module eShop.Prelude.Tests.TupleTests

open Expecto
open FsCheck
open eShop.Prelude

[<Tests>]
let tupleTests =
    testList "Tuple Tests" [
        testProperty "mapFst transforms only first element" <| fun (x: int) (y: string) ->
            let tuple = (x, y)
            let mapped = Tuple.mapFst string tuple
            snd mapped = y && fst mapped = string x

        testProperty "mapSnd transforms only second element" <| fun (x: int) (y: string) ->
            let tuple = (x, y)
            let mapped = Tuple.mapSnd int tuple
            fst mapped = x && (try snd mapped = int y with _ -> true)

        testProperty "Tuple.swap exchanges elements" <| fun x y ->
            let original = (x, y)
            let swapped = Tuple.swap original
            fst original = snd swapped && snd original = fst swapped
    ]
