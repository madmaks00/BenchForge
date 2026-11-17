using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;
using BenchForge.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using DataRow = BenchForge.Contracts.DataRow;

namespace BenchForge.Worker.CSharp.Databases;

public sealed class SqlDatabase(RunCase run) : IDatabase
{
    private DbConnection? _admin;
    private SqlSession? _loader;
    private string _connectionString = "";

    private DbConnection CreateConnection() => SqlConnections.Create(run.Target, _connectionString);

    public async Task InitializeAsync(CancellationToken token)
    {
        await SqlConnections.EnsureDatabaseAsync(run, token);
        _connectionString = SqlConnections.ConnectionString(run);
        var connection = CreateConnection();
        try { await connection.OpenAsync(token); await SqlConnections.ConfigureAsync(connection, run.Target, true, token); }
        catch { await connection.DisposeAsync(); throw; }
        _admin = connection;
        _loader = new(_admin, maintenance: true);
    }

    private async Task<long> ExecAsync(string sql, CancellationToken token, bool scalar = false)
    {
        await using var cmd = _admin!.CreateCommand();
        cmd.CommandText = sql; cmd.CommandTimeout = 600;
        return scalar ? Convert.ToInt64(await cmd.ExecuteScalarAsync(token)) : await cmd.ExecuteNonQueryAsync(token);
    }
    public async Task ResetAsync(CancellationToken token)
    {
        await ExecAsync("DROP TABLE IF EXISTS records", token);
        var textType = run.Target is TargetKind.MySql or TargetKind.MariaDb ? "LONGTEXT" : run.Target == TargetKind.SqlServer ? "NVARCHAR(MAX)" : "TEXT";
        await ExecAsync($"CREATE TABLE records (id BIGINT PRIMARY KEY, group_id INTEGER NOT NULL, amount_cents BIGINT NOT NULL, version BIGINT NOT NULL, payload {textType} NOT NULL)", token);
    }
    public Task LoadBatchAsync(DataRow[] rows, CancellationToken token) => _loader!.WriteBatchAsync(rows, token);
    public async Task PrepareAsync(CancellationToken token)
    {
        await ExecAsync("CREATE INDEX records_group_id ON records(group_id)", token);
        if (run.Target is TargetKind.MySql or TargetKind.MariaDb)
        {
            await using var cmd = _admin!.CreateCommand(); cmd.CommandText = "ANALYZE TABLE records"; cmd.CommandTimeout = 600;
            await using var reader = await cmd.ExecuteReaderAsync(token); while (await reader.ReadAsync(token)) { }
        }
        else await ExecAsync(run.Target == TargetKind.SqlServer ? "UPDATE STATISTICS records WITH FULLSCAN" : "ANALYZE records", token);
    }
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        await using var cmd = _admin!.CreateCommand(); cmd.CommandTimeout = 600;
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(id),0), COALESCE(SUM(amount_cents),0) FROM records";
        await using var reader = await cmd.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        return new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)));
    }
    public Task<long> CountAsync(CancellationToken token) => ExecAsync("SELECT COUNT(*) FROM records", token, true);
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        await using var command = _admin!.CreateCommand();
        command.CommandText = SqlConnections.VersionQuery(run.Target);
        var version = Convert.ToString(await command.ExecuteScalarAsync(token))!;
        var settings = new Dictionary<string, string> { ["connections"] = "one dedicated SQL connection per logical client; pooling=false",
            ["batch_transport"] = run.Target switch { TargetKind.SqlServer => "SqlBulkCopy; one batch in internal transaction; constraints checked", TargetKind.PostgreSql => "binary COPY", TargetKind.Sqlite => "prepared INSERT in transaction", _ => "multi-row parameterized INSERT" },
            ["prepared_queries"] = "true", ["generator"] = DataGenerator.Version };
        if (run.Target == TargetKind.PostgreSql)
            foreach (var name in new[] { "synchronous_commit", "fsync", "shared_buffers", "max_connections" })
            { command.CommandText = "SHOW " + name; settings[name] = Convert.ToString(await command.ExecuteScalarAsync(token))!; }
        else if (run.Target == TargetKind.Sqlite)
            foreach (var name in new[] { "journal_mode", "synchronous", "cache_size" })
            { command.CommandText = "PRAGMA " + name; settings[name] = Convert.ToString(await command.ExecuteScalarAsync(token))!; }
        else if (run.Target == TargetKind.SqlServer)
        {
            command.CommandText = "SELECT recovery_model_desc, delayed_durability_desc, is_read_committed_snapshot_on FROM sys.databases WHERE name=DB_NAME()";
            await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            settings["recovery_model"] = reader.GetString(0); settings["delayed_durability"] = reader.GetString(1);
            settings["read_committed_snapshot"] = reader.GetBoolean(2).ToString();
            settings["edition"] = "Developer (testing only)";
        }
        else
            foreach (var name in new[] { "innodb_flush_log_at_trx_commit", "innodb_buffer_pool_size", "max_connections" })
            { command.CommandText = "SELECT @@GLOBAL." + name; settings[name] = Convert.ToString(await command.ExecuteScalarAsync(token))!; }
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(_admin.GetType()), version, settings);
    }
    public async Task<IClientSession> OpenSessionAsync(CancellationToken token)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(token);
            await SqlConnections.ConfigureAsync(connection, run.Target, false, token);
            var session = new SqlSession(connection);
            await session.PrepareCommandsAsync(token);
            return session;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { if (_admin is not null) await _admin.DisposeAsync(); }
}

internal sealed class SqlSession(DbConnection connection, bool maintenance = false) : IClientSession
{
    private readonly Dictionary<Scenario, DbCommand> _commands = [];
    public async Task PrepareCommandsAsync(CancellationToken token)
    {
        foreach (var (scenario, sql) in new Dictionary<Scenario, string>
        {
            [Scenario.PointRead] = "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id=@id",
            [Scenario.RangeRead] = "SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>=@id ORDER BY id LIMIT 100",
            [Scenario.Aggregate] = "SELECT group_id,COUNT(*),SUM(amount_cents) FROM records WHERE group_id=@id GROUP BY group_id",
            [Scenario.Update] = "UPDATE records SET version=version+1 WHERE id=@id",
            [Scenario.Delete] = "DELETE FROM records WHERE id=@id",
            [Scenario.Insert] = "INSERT INTO records(id,group_id,amount_cents,version,payload) VALUES(@id,@group_id,@amount,@version,@payload)"
        })
        {
            var cmd = connection.CreateCommand(); cmd.CommandText = connection is SqlConnection && scenario == Scenario.RangeRead
                ? "SELECT TOP (100) id,group_id,amount_cents,version,payload FROM records WHERE id>=@id ORDER BY id" : sql; cmd.CommandTimeout = 10;
            Add(cmd, "@id", 0L, scenario == Scenario.Aggregate ? DbType.Int32 : DbType.Int64);
            if (scenario == Scenario.Insert)
            { Add(cmd, "@group_id", 0, DbType.Int32); Add(cmd, "@amount", 0L, DbType.Int64); Add(cmd, "@version", 0L, DbType.Int64); Add(cmd, "@payload", "", DbType.String); }
            await cmd.PrepareAsync(token);
            _commands.Add(scenario, cmd);
        }
    }
    private static void Add(DbCommand command, string name, object value, DbType type)
    { var p = command.CreateParameter(); p.ParameterName = name; p.DbType = type; p.Value = value; if (p is SqlParameter && type == DbType.String) p.Size = -1; command.Parameters.Add(p); }

    public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
    {
        if (scenario == Scenario.BatchInsert) { await WriteBatchAsync(batch!, token); return batch!.Length; }
        var cmd = _commands[scenario];
        cmd.Parameters[0].Value = scenario == Scenario.Aggregate ? (object)(int)((key - 1) % 100) : key;
        if (scenario == Scenario.Insert)
        { cmd.Parameters[0].Value = row!.Id; cmd.Parameters[1].Value = row.GroupId; cmd.Parameters[2].Value = row.AmountCents; cmd.Parameters[3].Value = row.Version; cmd.Parameters[4].Value = row.Payload; }
        if (scenario is Scenario.Update or Scenario.Delete or Scenario.Insert)
        {
            var affected = await cmd.ExecuteNonQueryAsync(token);
            if (affected != 1) throw new InvalidDataException("Операция записи не затронула одну строку.");
            return affected;
        }
        await using var reader = await cmd.ExecuteReaderAsync(token);
        long rows = 0;
        while (await reader.ReadAsync(token))
        {
            if (scenario != Scenario.Aggregate)
            {
                var id = reader.GetInt64(0); _ = reader.GetString(4);
                if (scenario == Scenario.PointRead && id != key) throw new InvalidDataException("Неверный ID в ответе.");
            }
            else { _ = reader.GetValue(1); _ = reader.GetValue(2); }
            rows++;
        }
        if (rows == 0 || scenario == Scenario.PointRead && rows != 1) throw new InvalidDataException("Неверное число строк в ответе.");
        return rows;
    }

    public async Task WriteBatchAsync(DataRow[] rows, CancellationToken token)
    {
        if (connection is NpgsqlConnection pg)
        {
            await using var importer = await pg.BeginBinaryImportAsync("COPY records(id,group_id,amount_cents,version,payload) FROM STDIN (FORMAT BINARY)", token);
            importer.Timeout = TimeSpan.FromSeconds(maintenance ? 600 : 10);
            foreach (var row in rows)
            {
                await importer.StartRowAsync(token);
                await importer.WriteAsync(row.Id, NpgsqlDbType.Bigint, token); await importer.WriteAsync(row.GroupId, NpgsqlDbType.Integer, token);
                await importer.WriteAsync(row.AmountCents, NpgsqlDbType.Bigint, token); await importer.WriteAsync(row.Version, NpgsqlDbType.Bigint, token);
                await importer.WriteAsync(row.Payload, NpgsqlDbType.Text, token);
            }
            await importer.CompleteAsync(token);
        }
        else if (connection is SqlConnection server)
        {
            using var table = new DataTable();
            table.Columns.Add("id", typeof(long)); table.Columns.Add("group_id", typeof(int)); table.Columns.Add("amount_cents", typeof(long));
            table.Columns.Add("version", typeof(long)); table.Columns.Add("payload", typeof(string));
            foreach (var row in rows) table.Rows.Add(row.Id, row.GroupId, row.AmountCents, row.Version, row.Payload);
            using var bulk = new SqlBulkCopy(server, SqlBulkCopyOptions.UseInternalTransaction | SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls, null)
                { DestinationTableName = "records", BatchSize = rows.Length, BulkCopyTimeout = maintenance ? 600 : 10 };
            foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(table, token);
        }
        else if (connection is SqliteConnection)
        {
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandTimeout = maintenance ? 600 : 10;
            cmd.CommandText = "INSERT INTO records VALUES(@id,@group_id,@amount,@version,@payload)";
            Add(cmd, "@id", 0L, DbType.Int64); Add(cmd, "@group_id", 0, DbType.Int32); Add(cmd, "@amount", 0L, DbType.Int64);
            Add(cmd, "@version", 0L, DbType.Int64); Add(cmd, "@payload", "", DbType.String); await cmd.PrepareAsync(token);
            foreach (var row in rows)
            {
                cmd.Parameters[0].Value = row.Id; cmd.Parameters[1].Value = row.GroupId; cmd.Parameters[2].Value = row.AmountCents;
                cmd.Parameters[3].Value = row.Version; cmd.Parameters[4].Value = row.Payload; await cmd.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        else
        {
            await using var cmd = connection.CreateCommand(); cmd.CommandTimeout = maintenance ? 600 : 10;
            var sql = new StringBuilder("INSERT INTO records VALUES ");
            for (var i = 0; i < rows.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append($"(@i{i},@g{i},@a{i},@v{i},@p{i})");
                var row = rows[i]; Add(cmd, $"@i{i}", row.Id, DbType.Int64); Add(cmd, $"@g{i}", row.GroupId, DbType.Int32);
                Add(cmd, $"@a{i}", row.AmountCents, DbType.Int64); Add(cmd, $"@v{i}", row.Version, DbType.Int64); Add(cmd, $"@p{i}", row.Payload, DbType.String);
            }
            cmd.CommandText = sql.ToString(); await cmd.ExecuteNonQueryAsync(token);
        }
    }
    public async ValueTask DisposeAsync()
    { foreach (var command in _commands.Values) await command.DisposeAsync(); await connection.DisposeAsync(); }
}
