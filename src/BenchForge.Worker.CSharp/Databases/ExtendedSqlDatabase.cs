using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;
using BenchForge.Contracts;
using DuckDB.NET.Data;
using Oracle.ManagedDataAccess.Client;
using DataRow = BenchForge.Contracts.DataRow;

namespace BenchForge.Worker.CSharp.Databases;

public sealed class ExtendedSqlDatabase(RunCase run) : IDatabase
{
    private DbConnection? _admin;
    private string _connectionString = "";
    private ExtendedSqlSession? _loader;
    public async Task InitializeAsync(CancellationToken token)
    {
        await SqlConnections.EnsureDatabaseAsync(run, token);
        _connectionString = SqlConnections.ConnectionString(run);
        var connection = SqlConnections.Create(run.Target, _connectionString);
        try { await connection.OpenAsync(token); await SqlConnections.ConfigureAsync(connection, run.Target, true, token, run.Config); }
        catch { await connection.DisposeAsync(); throw; }
        _admin = connection; _loader = new(connection, run, true);
    }
    private async Task<object?> ExecuteAsync(string sql, bool scalar, CancellationToken token)
    {
        await using var command = _admin!.CreateCommand(); SqlConnections.SetText(command, sql); command.CommandTimeout = 600;
        return scalar ? await command.ExecuteScalarAsync(token) : await command.ExecuteNonQueryAsync(token);
    }
    public async Task ResetAsync(CancellationToken token)
    {
        if (run.Target == TargetKind.Oracle)
            await ExecuteAsync("BEGIN EXECUTE IMMEDIATE 'DROP TABLE records PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;", false, token);
        else await ExecuteAsync("DROP TABLE IF EXISTS records", false, token);
        var ddl = run.Target == TargetKind.Oracle
            ? "CREATE TABLE records(id NUMBER(19) PRIMARY KEY, group_id NUMBER(10) NOT NULL, amount_cents NUMBER(19) NOT NULL, version NUMBER(19) NOT NULL, payload CLOB NOT NULL)"
            : "CREATE TABLE records(id BIGINT PRIMARY KEY, group_id INTEGER NOT NULL, amount_cents BIGINT NOT NULL, version BIGINT NOT NULL, payload TEXT NOT NULL)";
        await ExecuteAsync(ddl, false, token);
    }
    public Task LoadBatchAsync(DataRow[] rows, CancellationToken token) => _loader!.WriteBatchAsync(rows, token);
    public async Task PrepareAsync(CancellationToken token)
    {
        await ExecuteAsync("CREATE INDEX records_group_id ON records(group_id)", false, token);
        await ExecuteAsync(run.Target switch
        {
            TargetKind.Oracle => "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'RECORDS'); END;",
            TargetKind.CockroachDb => "CREATE STATISTICS records_stats FROM records", _ => "ANALYZE records"
        }, false, token);
    }
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        await using var command = _admin!.CreateCommand(); command.CommandTimeout = 600;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(id),0), COALESCE(SUM(amount_cents),0) FROM records";
        await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        return new(SqlScalars.Int64(reader.GetValue(0)), SqlScalars.Int64(reader.GetValue(1)), SqlScalars.Int64(reader.GetValue(2)));
    }
    public async Task<long> CountAsync(CancellationToken token) => SqlScalars.Int64(await ExecuteAsync("SELECT COUNT(*) FROM records", true, token));
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        var settings = new Dictionary<string, string>
        {
            ["connections"] = "one SQL connection per logical client; pooling disabled",
            ["prepared_queries"] = "true (provider semantics)", ["generator"] = DataGenerator.Version,
            ["batch_transport"] = run.Target switch { TargetKind.DuckDb => "DuckDB appender inside batch transaction", TargetKind.Oracle => "ODP.NET array binding inside batch transaction", _ => "multi-row parameterized INSERT" },
            ["transaction_retries"] = "none; conflicts/deadlocks/restarts are counted as failed operations"
        };
        if (run.Target == TargetKind.DuckDb)
        {
            settings["topology"] = "embedded OLAP; database and generator share process RAM/CPU";
            settings["duckdb_memory_limit_mb"] = run.Config.MemoryMb.ToString(); settings["threads"] = ((int)Math.Ceiling(run.Config.CpuLimit)).ToString();
            settings["limits"] = "DuckDB memory manager budget and query thread count; not an OS process quota";
        }
        if (run.Target == TargetKind.CockroachDb) settings["topology"] = "start-single-node; replication disabled; insecure loopback test profile; default SERIALIZABLE";
        if (run.Target == TargetKind.Oracle) settings["edition"] = "Oracle Database Free; edition resource limits apply; own DBLAB schema in FREEPDB1";
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(_admin!.GetType()), Convert.ToString(await ExecuteAsync(SqlConnections.VersionQuery(run.Target), true, token))!, settings);
    }
    public async Task<IClientSession> OpenSessionAsync(CancellationToken token)
    {
        var connection = SqlConnections.Create(run.Target, _connectionString);
        try
        {
            await connection.OpenAsync(token); await SqlConnections.ConfigureAsync(connection, run.Target, false, token, run.Config);
            var session = new ExtendedSqlSession(connection, run); await session.PrepareAsync(token); return session;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { if (_admin is not null) await _admin.DisposeAsync(); }
}

internal sealed class ExtendedSqlSession(DbConnection connection, RunCase run, bool maintenance = false) : IClientSession
{
    private readonly Dictionary<Scenario, DbCommand> _commands = [];
    public async Task PrepareAsync(CancellationToken token)
    {
        foreach (var scenario in Catalog.Get(run.Target).Scenarios.Where(s => s is not (Scenario.Mixed or Scenario.BatchInsert)))
        {
            var sql = scenario switch
            {
                Scenario.PointRead => "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=@id",
                Scenario.RangeRead => "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=@id ORDER BY id " + (run.Target == TargetKind.Oracle ? "FETCH FIRST 100 ROWS ONLY" : "LIMIT 100"),
                Scenario.Aggregate => "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=@id GROUP BY group_id",
                Scenario.AnalyticalScan => "SELECT COUNT(*),SUM(amount_cents),AVG(amount_cents) FROM records WHERE id>=@id AND id<@finish",
                Scenario.Update => "UPDATE records SET version=version+1 WHERE id=@id", Scenario.Delete => "DELETE FROM records WHERE id=@id",
                _ => "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(@id,@group_id,@amount,@version,@payload)"
            };
            var command = connection.CreateCommand(); SqlConnections.SetText(command, sql); command.CommandTimeout = 10;
            Add(command, "id", 0L, scenario == Scenario.Aggregate ? SqlValueType.Int32 : SqlValueType.Int64);
            if (scenario == Scenario.AnalyticalScan) Add(command, "finish", 0L, SqlValueType.Int64);
            if (scenario == Scenario.Insert)
            { Add(command, "group_id", 0, SqlValueType.Int32); Add(command, "amount", 0L, SqlValueType.Int64); Add(command, "version", 0L, SqlValueType.Int64); Add(command, "payload", "", SqlValueType.Text); }
            await command.PrepareAsync(token); _commands.Add(scenario, command);
        }
    }
    private static void Add(DbCommand command, string name, object value, SqlValueType type)
    {
        var parameter = CustomSqlDatabase.AddParameter(command, name, type); parameter.Value = value;
        if (parameter is OracleParameter oracle && name == "payload") { oracle.OracleDbType = OracleDbType.Clob; oracle.Size = 0; }
    }
    public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
    {
        if (scenario == Scenario.BatchInsert) { await WriteBatchAsync(batch!, token); return batch!.Length; }
        var command = _commands[scenario]; command.Parameters[0].Value = scenario == Scenario.Aggregate ? (object)(int)((key - 1) % 100) : key;
        if (scenario == Scenario.AnalyticalScan) command.Parameters[1].Value = key + Math.Max(100, run.Rows / 100);
        if (scenario == Scenario.Insert)
        { command.Parameters[0].Value = row!.Id; command.Parameters[1].Value = row.GroupId; command.Parameters[2].Value = row.AmountCents; command.Parameters[3].Value = row.Version; command.Parameters[4].Value = row.Payload;
            if (command.Parameters[4] is OracleParameter oracle) oracle.Size = row.Payload.Length; }
        if (scenario is Scenario.Insert or Scenario.Update or Scenario.Delete)
        { var count = await command.ExecuteNonQueryAsync(token); if (count != 1) throw new InvalidDataException("Запись не затронула ровно одну строку."); return count; }
        await using var reader = await command.ExecuteReaderAsync(token); long rows = 0;
        while (await reader.ReadAsync(token))
        {
            for (var index = 0; index < reader.FieldCount; index++)
                if (index == 4 && scenario is Scenario.PointRead or Scenario.RangeRead)
                {
                    var payload = reader.GetString(index);
                    if (payload.Length != run.Config.PayloadBytes) throw new InvalidDataException("Текстовое поле было обрезано при записи или чтении.");
                }
                else _ = reader.GetValue(index);
            if (scenario == Scenario.PointRead && SqlScalars.Int64(reader.GetValue(0)) != key) throw new InvalidDataException("Неверный ключ в ответе.");
            rows++;
        }
        if (rows == 0 || scenario == Scenario.PointRead && rows != 1) throw new InvalidDataException("Неверное число строк в ответе.");
        return rows;
    }
    public async Task WriteBatchAsync(DataRow[] rows, CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        if (connection is DuckDBConnection duck)
        {
            using var appender = duck.CreateAppender("records");
            foreach (var row in rows)
            { token.ThrowIfCancellationRequested(); appender.CreateRow().AppendValue(row.Id).AppendValue(row.GroupId).AppendValue(row.AmountCents).AppendValue(row.Version).AppendValue(row.Payload).EndRow(); }
            appender.Close();
        }
        else if (connection is OracleConnection)
        {
            await using var command = (OracleCommand)connection.CreateCommand(); command.Transaction = (OracleTransaction)transaction;
            command.BindByName = true; command.ArrayBindCount = rows.Length; command.CommandTimeout = maintenance ? 600 : 10;
            command.CommandText = "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(:id,:group_id,:amount,:version,:payload)";
            command.Parameters.Add("id", OracleDbType.Int64, rows.Select(r => r.Id).ToArray(), ParameterDirection.Input);
            command.Parameters.Add("group_id", OracleDbType.Int32, rows.Select(r => r.GroupId).ToArray(), ParameterDirection.Input);
            command.Parameters.Add("amount", OracleDbType.Int64, rows.Select(r => r.AmountCents).ToArray(), ParameterDirection.Input);
            command.Parameters.Add("version", OracleDbType.Int64, rows.Select(r => r.Version).ToArray(), ParameterDirection.Input);
            var payload = command.Parameters.Add("payload", OracleDbType.Clob, rows.Select(r => r.Payload).ToArray(), ParameterDirection.Input);
            payload.ArrayBindSize = rows.Select(r => r.Payload.Length).ToArray();
            await command.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = maintenance ? 600 : 10;
            var sql = new StringBuilder("INSERT INTO records VALUES ");
            for (var index = 0; index < rows.Length; index++)
            {
                if (index > 0) sql.Append(','); sql.Append($"(@i{index},@g{index},@a{index},@v{index},@p{index})");
                var row = rows[index]; Add(command, "i" + index, row.Id, SqlValueType.Int64); Add(command, "g" + index, row.GroupId, SqlValueType.Int32);
                Add(command, "a" + index, row.AmountCents, SqlValueType.Int64); Add(command, "v" + index, row.Version, SqlValueType.Int64); Add(command, "p" + index, row.Payload, SqlValueType.Text);
            }
            command.CommandText = sql.ToString(); await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
    public async ValueTask DisposeAsync() { foreach (var command in _commands.Values) await command.DisposeAsync(); await connection.DisposeAsync(); }
}
