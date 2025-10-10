[<RequireQualifiedAccess>]
module eShop.Prelude.Tests.TaskResult

open Expecto
open FsToolkit.ErrorHandling

let tests =
    testList
        "TaskResult Tests"
        [ testProperty "Left identity: return a >>= f = f a"
          <| fun x ->
              task {
                  let f a =
                      if a > 0 then
                          TaskResult.ok (a * 2)
                      else
                          TaskResult.error "negative"

                  let! left = TaskResult.bind f (TaskResult.ok x)
                  let! right = f x
                  Expect.equal left right "Should be equal"
              }

          testProperty "Right identity: m >>= return = m"
          <| fun x ->
              task {
                  let m =
                      if x > 0 then
                          TaskResult.ok x
                      else
                          TaskResult.error "negative"

                  let! left = TaskResult.bind TaskResult.ok m
                  let! right = m
                  Expect.equal left right "Should be equal"
              }

          testProperty "Map preserves success/error status"
          <| fun x ->
              task {
                  let m =
                      if x > 0 then
                          TaskResult.ok x
                      else
                          TaskResult.error "negative"

                  let mapped = TaskResult.map ((*) 2) m
                  let! left = mapped
                  let! right = m
                  Expect.equal left right "Should be equal"
              }

          ]
