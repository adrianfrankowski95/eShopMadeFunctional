module eShop.Prelude.Tests.OptionTests

open Expecto
open FsCheck
open eShop.Prelude

[<Tests>]
let optionTests =
    testList "Option Tests" [
        testProperty "Left identity: return a >>= f = f a" <| fun x ->
            let f a = if a > 0 then Some (a * 2) else None
            let left = Option.bind f (Some x)
            let right = f x
            left = right

        testProperty "Right identity: m >>= return = m" <| fun x ->
            let m = if x > 0 then Some x else None
            let left = Option.bind Some m
            let right = m
            left = right

        testProperty "Associativity: (m >>= f) >>= g = m >>= (\\x -> f x >>= g)" <| fun x ->
            let f a = if a > 0 then Some (a * 2) else None
            let g a = if a < 100 then Some (a + 1) else None
            let m = Some x
            let left = Option.bind g (Option.bind f m)
            let right = Option.bind (fun x -> Option.bind g (f x)) m
            left = right
            
        testProperty "Option.defaultValue always returns a value" <| fun x defaultVal ->
            let opt = if x % 2 = 0 then Some x else None
            let result = Option.defaultValue defaultVal opt
            match opt with
            | Some v -> result = v
            | None -> result = defaultVal

        testProperty "Option.map preserves structure" <| fun x ->
            let opt = if x % 2 = 0 then Some x else None
            let f = (*) 2
            let mapped = Option.map f opt
            match opt, mapped with
            | Some v, Some m -> m = f v
            | None, None -> true
            | _ -> false
    ]
