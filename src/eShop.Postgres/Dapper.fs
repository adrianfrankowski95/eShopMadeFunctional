[<RequireQualifiedAccess>]
module eShop.Postgres.Dapper

open System
open Dapper
open FsToolkit.ErrorHandling

let private (|SqlSession|) =
    function
    | SqlSession.Sustained dbTransaction -> (dbTransaction.Connection, dbTransaction)
    | SqlSession.Standalone dbConnection -> (dbConnection, null)

let inline private toAsyncResult x =
    try
        x |> Async.AwaitTask |> Async.map Ok
    with e ->
        e |> SqlException |> AsyncResult.error

let query<'t> (SqlSession(dbConnection, transaction)) sql param =
    dbConnection.QueryAsync<'t>(sql, param, transaction) |> toAsyncResult

let execute (SqlSession(dbConnection, transaction)) sql param =
    dbConnection.ExecuteAsync(sql, param, transaction) |> toAsyncResult

let executeScalar<'t> (SqlSession(dbConnection, transaction)) sql param =
    dbConnection.ExecuteScalarAsync<'t>(sql, param, transaction) |> toAsyncResult

[<RequireQualifiedAccess>]
module TypeHandlers =
    type OptionHandler<'T>() =
        inherit SqlMapper.TypeHandler<option<'T>>()

        override _.SetValue(param, value) =
            let valueOrNull =
                match value with
                | Some x -> box x
                | None -> null

            param.Value <- valueOrNull

        override _.Parse value =
            if isNull value || value = box DBNull.Value then
                None
            else
                Some(value :?> 'T)

    let register () =
        SqlMapper.AddTypeHandler(OptionHandler<Guid>())
        SqlMapper.AddTypeHandler(OptionHandler<byte>())
        SqlMapper.AddTypeHandler(OptionHandler<int16>())
        SqlMapper.AddTypeHandler(OptionHandler<int>())
        SqlMapper.AddTypeHandler(OptionHandler<int64>())
        SqlMapper.AddTypeHandler(OptionHandler<uint16>())
        SqlMapper.AddTypeHandler(OptionHandler<uint>())
        SqlMapper.AddTypeHandler(OptionHandler<uint64>())
        SqlMapper.AddTypeHandler(OptionHandler<float>())
        SqlMapper.AddTypeHandler(OptionHandler<decimal>())
        SqlMapper.AddTypeHandler(OptionHandler<float32>())
        SqlMapper.AddTypeHandler(OptionHandler<string>())
        SqlMapper.AddTypeHandler(OptionHandler<char>())
        SqlMapper.AddTypeHandler(OptionHandler<DateTime>())
        SqlMapper.AddTypeHandler(OptionHandler<DateTimeOffset>())
        SqlMapper.AddTypeHandler(OptionHandler<bool>())
        SqlMapper.AddTypeHandler(OptionHandler<TimeSpan>())
        SqlMapper.AddTypeHandler(OptionHandler<byte[]>())