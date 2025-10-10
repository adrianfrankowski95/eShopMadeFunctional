module eShop.Prelude.Tests.OperatorTests

open Expecto
open FsCheck
open eShop.Prelude

[<Tests>]
let operatorTests =
    testList "Operator Tests" [
        testProperty "Result.map (|>>) composition works" <| fun x ->
            let f a = a * 2
            let g a = a + 1
            let r = Ok x
            let left = r |>> f |>> g
            let right = r |>> (f >> g)
            left = right
            
        testProperty "Result.bind (>>=) respects monad laws" <| fun x ->
            let f a = if a > 0 then Ok (a * 2) else Error "negative"
            let g a = if a < 100 then Ok (a + 1) else Error "too large"
            let m = Ok x
            let left = (m >>= f) >>= g
            let right = m >>= (fun x -> f x >>= g)
            left = right
    ]
