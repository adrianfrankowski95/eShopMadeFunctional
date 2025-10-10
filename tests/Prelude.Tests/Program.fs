module Program

open Expecto
open eShop.Prelude.Tests

[<EntryPoint>]
let main argv =
    [ Result.tests
      TaskResult.tests
      Either.tests
      Operators.tests
      Option.tests
      Map.tests
      Tuple.tests ]
    |> testList "Prelude Tests"
    |> runTestsWithCLIArgs [] argv
