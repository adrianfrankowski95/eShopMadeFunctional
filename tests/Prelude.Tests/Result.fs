[<RequireQualifiedAccess>]
module eShop.Prelude.Tests.Result

open Expecto

let tests =
    testList "Result Monad Tests" [
        testProperty "Left identity: return a >>= f = f a" <| fun x ->
            let f a = if a > 0 then Ok (a * 2) else Error "negative"
            let left = Result.bind f (Ok x)
            let right = f x
            Expect.equal left right "Should be equal"

        testProperty "Right identity: m >>= return = m" <| fun x ->
            let m = if x > 0 then Ok x else Error "negative"
            let left = Result.bind Ok m
            let right = m
            Expect.equal left right "Should be equal"

        testProperty "Associativity: (m >>= f) >>= g = m >>= (\\x -> f x >>= g)" <| fun x ->
            let f a = if a > 0 then Ok (a * 2) else Error "negative"
            let g a = if a < 100 then Ok (a + 1) else Error "too large"
            let m = Ok x
            let left = Result.bind g (Result.bind f m)
            let right = Result.bind (fun x -> Result.bind g (f x)) m
            Expect.equal left right "Should be equal"
    ]
