[<RequireQualifiedAccess>]
module eShop.Prelude.ValueTask

open System.Threading.Tasks
open FsToolkit.ErrorHandling

let inline asTask (vt: ValueTask) = vt.AsTask() |> Task.ofUnit
