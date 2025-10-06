[<RequireQualifiedAccess>]
module eShop.Prelude.Task

open System.Threading.Tasks

let inline createColdSeq (f: 'a -> Task<_>) (x: #seq<_>) = x |> Seq.map (fun x -> fun () -> f x)

let inline sequential (tasks: #seq<unit -> Task<_>>) =
    task {
        let count = tasks |> Seq.length
        let results = count |> Array.zeroCreate

        for i in 0 .. count - 1 do
            let! result = tasks |> Seq.item i |> (fun t -> t ())
            results[i] <- result

        return results
    }

let inline getResultSynchronously (t: Task<_>) = t.GetAwaiter().GetResult()