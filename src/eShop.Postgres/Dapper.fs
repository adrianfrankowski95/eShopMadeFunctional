[<RequireQualifiedAccess>]
module eShop.Postgres.Dapper

open System
open System.Data
open Dapper
open FsToolkit.ErrorHandling

let private (|SqlSession|) =
    function
    | SqlSession.Sustained dbTransaction -> (dbTransaction.Connection, dbTransaction)
    | SqlSession.Standalone getDbConnection -> (getDbConnection (), null)

let inline private toAsyncResult f =
    try
        f () |> Async.AwaitTask |> Async.map Ok
    with e ->
        e |> SqlException |> AsyncResult.error

let query<'t> (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.QueryAsync<'t>(sql, param, transaction))
    |> toAsyncResult

let execute (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.ExecuteAsync(sql, param, transaction)) |> toAsyncResult

let executeScalar<'t> (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.ExecuteScalarAsync<'t>(sql, param, transaction))
    |> toAsyncResult

[<RequireQualifiedAccess>]
module TypeHandlers =
    type DateOnlyHandler<'t>() =
        inherit SqlMapper.TypeHandler<DateOnly>()

        override _.SetValue(param, value) =
            param.Value <- value.ToDateTime(TimeOnly.MinValue)
            param.DbType <- DbType.Date

        override _.Parse value =
            match value with
            | :? DateOnly as dateOnly -> dateOnly
            | _ -> DateOnly.FromDateTime(value :?> DateTime)

    type DateOnlyOptionTypeHandler() =
        inherit SqlMapper.TypeHandler<option<DateOnly>>()

        override _.SetValue(param, value) =
            match value with
            | Some dateOnly -> param.Value <- dateOnly
            | None -> param.Value <- null

        override _.Parse value =
            if isNull value || value = box DBNull.Value then
                None
            else
                DateOnly.FromDateTime(value :?> DateTime) |> Some

    type OptionHandler<'t>() =
        inherit SqlMapper.TypeHandler<option<'t>>()

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
                Some(value :?> 't)

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
        SqlMapper.AddTypeHandler(DateOnlyHandler())
        SqlMapper.AddTypeHandler(DateOnlyOptionTypeHandler())
        SqlMapper.AddTypeMap(typeof<DateTime>, DbType.DateTime2)
        SqlMapper.AddTypeMap(typeof<DateTimeOffset>, DbType.DateTimeOffset)
        SqlMapper.AddTypeMap(typeof<DateOnly>, DbType.Date)
