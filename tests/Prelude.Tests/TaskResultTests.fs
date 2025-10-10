module eShop.Prelude.Tests.TaskResultTests

open Expecto
open FsCheck
open eShop.Prelude
open System.Threading.Tasks

let taskResultGen: Gen<TaskResult<int, string>> =
    gen {
        let! value = Arb.generate<int>
        let! isError = Arb.generate<bool>
        return 
            if isError then TaskResult.ofError "error"
            else TaskResult.ofSuccess value
    }

type TaskResultArbitrary =
    static member TaskResult() =
        { new Arbitrary<TaskResult<int, string>>() with
            override x.Generator = taskResultGen }

[<Tests>]
let taskResultTests =
    testList "TaskResult Tests" [
        testProperty "Left identity: return a >>= f = f a" <| fun x ->
            let f a = if a > 0 then TaskResult.ofSuccess (a * 2) else TaskResult.ofError "negative"
            let left = TaskResult.bind f (TaskResult.ofSuccess x)
            let right = f x
            Task.WaitAll([left.AsTask(); right.AsTask()])
            left.Result = right.Result

        testProperty "Right identity: m >>= return = m" <| fun x ->
            let m = if x > 0 then TaskResult.ofSuccess x else TaskResult.ofError "negative"
            let left = TaskResult.bind TaskResult.ofSuccess m
            let right = m
            Task.WaitAll([left.AsTask(); right.AsTask()])
            left.Result = right.Result

        testProperty "Map preserves success/error status" <| fun x ->
            let m = if x > 0 then TaskResult.ofSuccess x else TaskResult.ofError "negative"
            let mapped = TaskResult.map ((*) 2) m
            Task.WaitAll([m.AsTask(); mapped.AsTask()])
            (m.Result.IsOk = mapped.Result.IsOk)
    ]
