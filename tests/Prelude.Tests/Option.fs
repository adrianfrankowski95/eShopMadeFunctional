[<RequireQualifiedAccess>]
module eShop.Prelude.Tests.Option

open Expecto
open Expecto.Flip
open FsCheck
open eShop.Prelude

let tests =
    testList
        "Option Tests"
        [ testList
              "ofList"
              [ testProperty "Given empty list Should return None"
                <| fun () ->
                    let result = [] |> Option.ofList
                    result |> Expect.isNone "Should return None"

                testProperty "Given non empty list Should return Some head and tail"
                <| fun (NonEmptyArray x: NonEmptyArray<int>) ->
                    let list = x |> List.ofArray
                    let result = list |> Option.ofList

                    result
                    |> Expect.wantSome "Should return Some"
                    |> Expect.equal "Should be equal to the list" (list.Head, list.Tail) ]

          testList
              "ofMap"
              [ testProperty "Given empty map Should return None"
                <| fun () ->
                    let result = Map.empty |> Option.ofMap
                    result |> Expect.isNone "Should return None"

                testProperty "Given non empty map Should return Some"
                <| fun (NonEmptyArray x: NonEmptyArray<int * int>) ->
                    let map = x |> Map.ofArray
                    let result = map |> Option.ofMap

                    result
                    |> Expect.wantSome "Should return Some"
                    |> Expect.equal "Should be equal to the map" map ] ]
