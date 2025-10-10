module eShop.Prelude.Tests.MapTests

open Expecto
open FsCheck
open eShop.Prelude

[<Tests>]
let mapTests =
    testList "Map Tests" [
        testProperty "tryFind returns None for non-existent key" <| fun k v ->
            let m = Map.empty |> Map.add (k + 1) v
            Map.tryFind k m = None

        testProperty "add then find returns added value" <| fun k v ->
            let m = Map.empty |> Map.add k v
            Map.tryFind k m = Some v

        testProperty "remove then find returns None" <| fun k v ->
            let m = Map.empty |> Map.add k v |> Map.remove k
            Map.tryFind k m = None

        testProperty "update preserves other entries" <| fun k v1 v2 k2 v3 ->
            k <> k2 ==>
                let m = Map.empty |> Map.add k v1 |> Map.add k2 v3
                let updated = Map.add k v2 m
                Map.tryFind k2 updated = Some v3
    ]
