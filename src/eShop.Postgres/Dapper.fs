[<RequireQualifiedAccess>]
module eShop.Postgres.Dapper

open Dapper
open FsToolkit.ErrorHandling

let private (|SqlSession|) =
    function
    | Sustained dbTransaction -> (dbTransaction.Connection, dbTransaction)
    | Standalone dbConnection -> (dbConnection, null)

let inline private toAsyncResult x =
    try
        x |> Async.AwaitTask |> Async.map Ok
    with e ->
        e |> SqlException |> AsyncResult.error

let query<'t> sql param (SqlSession(dbConnection, transaction)) =
    dbConnection.QueryAsync<'t>(sql, param, transaction) |> toAsyncResult

let execute sql param (SqlSession(dbConnection, transaction)) =
    dbConnection.ExecuteAsync(sql, param, transaction) |> toAsyncResult

let executeScalar<'t> sql param (SqlSession(dbConnection, transaction)) =
    dbConnection.ExecuteScalarAsync<'t>(sql, param, transaction) |> toAsyncResult
