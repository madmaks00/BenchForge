using System.Data.Common;
using DuckDB.NET.Data;
using Oracle.ManagedDataAccess.Client;
using BenchForge.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace BenchForge.Worker.CSharp.Databases;

internal static class SqlConnections
{
    public static DbConnection Create(TargetKind target, string connectionString) => target switch
    { TargetKind.DuckDb => new DuckDBConnection(connectionString), TargetKind.Oracle => new OracleConnection(connectionString), TargetKind.CockroachDb => new NpgsqlConnection(connectionString), TargetKind.SqlServer => new SqlConnection(connectionString), TargetKind.PostgreSql => new NpgsqlConnection(connectionString), TargetKind.Sqlite => new SqliteConnection(connectionString), _ => new MySqlConnection(connectionString) };
    public static string ConnectionString(RunCase run, bool admin = false)
    {
        var ep = run.Endpoint;
        return run.Target switch
        {
            TargetKind.DuckDb => new DbConnectionStringBuilder { ["DataSource"] = ep.FilePath }.ConnectionString,
            TargetKind.Oracle => new OracleConnectionStringBuilder { DataSource = $"{ep.Host}:{ep.Port}/{ep.Database}", UserID = admin ? "SYSTEM" : ep.Username,
                Password = ep.Password, Pooling = false, StatementCacheSize = 0 }.ConnectionString,
            TargetKind.CockroachDb => new NpgsqlConnectionStringBuilder { Host = ep.Host, Port = ep.Port, Username = ep.Username, Database = ep.Database,
                SslMode = SslMode.Disable, Pooling = false, Timeout = 5, CommandTimeout = 10 }.ConnectionString,
            TargetKind.SqlServer => new SqlConnectionStringBuilder { DataSource = $"{ep.Host},{ep.Port}", UserID = ep.Username, Password = ep.Password,
                InitialCatalog = ep.Database, Pooling = false, ConnectTimeout = 5, ConnectRetryCount = 0, Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true, ApplicationName = "BenchForge" }.ConnectionString,
            TargetKind.PostgreSql => new NpgsqlConnectionStringBuilder { Host = ep.Host, Port = ep.Port, Username = ep.Username, Password = ep.Password,
                Database = ep.Database, Pooling = false, Timeout = 5, CommandTimeout = 10, Options = "-c synchronous_commit=on" }.ConnectionString,
            TargetKind.Sqlite => new SqliteConnectionStringBuilder { DataSource = ep.FilePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ConnectionString,
            _ => new MySqlConnectionStringBuilder { Server = ep.Host, Port = (uint)ep.Port, UserID = ep.Username, Password = ep.Password,
                Database = ep.Database, Pooling = false, ConnectionTimeout = 5, DefaultCommandTimeout = 10, AllowPublicKeyRetrieval = true, SslMode = MySqlSslMode.Disabled }.ConnectionString
        };
    }
    public static string VersionQuery(TargetKind target) => target switch
    { TargetKind.DuckDb => "SELECT version()", TargetKind.Oracle => "SELECT version_full FROM product_component_version FETCH FIRST 1 ROWS ONLY", TargetKind.Sqlite => "SELECT sqlite_version()", TargetKind.SqlServer => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", _ => "SELECT VERSION()" };

    public static async Task EnsureDatabaseAsync(RunCase run, CancellationToken token)
    {
        if (run.Target == TargetKind.CockroachDb)
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString(run)) { Database = "defaultdb" };
            await using var connection = new NpgsqlConnection(builder.ConnectionString); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand(); command.CommandText = "CREATE DATABASE IF NOT EXISTS dblab";
            await command.ExecuteNonQueryAsync(token); return;
        }
        if (run.Target == TargetKind.Oracle)
        {
            await using var connection = new OracleConnection(ConnectionString(run, true)); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand(); command.CommandTimeout = 60;
            command.CommandText = "SELECT COUNT(*) FROM all_users WHERE username='DBLAB'";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 0)
            {
                var password = run.Endpoint.Password.Replace("\"", "\"\"");
                command.CommandText = "CREATE USER DBLAB IDENTIFIED BY \"" + password + "\""; await command.ExecuteNonQueryAsync(token);
            }
            command.CommandText = "GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, UNLIMITED TABLESPACE TO DBLAB";
            await command.ExecuteNonQueryAsync(token); return;
        }
        if (run.Target == TargetKind.SqlServer)
        {
            if (run.Endpoint.Database != "dblab") throw new InvalidDataException("SQL Server ожидает собственную тестовую базу dblab.");
            var builder = new SqlConnectionStringBuilder(ConnectionString(run)) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand(); command.CommandTimeout = 60;
            command.CommandText = "IF DB_ID(N'dblab') IS NULL BEGIN CREATE DATABASE [dblab]; END; ALTER DATABASE [dblab] SET RECOVERY SIMPLE; ALTER DATABASE [dblab] SET DELAYED_DURABILITY = DISABLED;";
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public static void SetText(DbCommand command, string sql)
    {
        if (command is OracleCommand oracle) oracle.BindByName = true;
        command.CommandText = SqlWorkloadText.BindMarkers(sql, command is OracleCommand ? TargetKind.Oracle : command is DuckDBCommand ? TargetKind.DuckDb : TargetKind.PostgreSql);
    }
    public static string ParameterName(DbCommand command, string name) => command is OracleCommand or DuckDBCommand ? name.TrimStart('@', ':', '$') : "@" + name.TrimStart('@', ':', '$');

    public static async Task ConfigureAsync(DbConnection connection, TargetKind target, bool initialize, CancellationToken token, BenchmarkConfig? config = null)
    {
        if (target == TargetKind.DuckDb)
        {
            if (initialize)
            {
                await using var settings = connection.CreateCommand();
                settings.CommandText = $"SET memory_limit='{config?.MemoryMb ?? 2048}MB'; SET threads={(int)Math.Ceiling(config?.CpuLimit ?? 2)};";
                await settings.ExecuteNonQueryAsync(token);
            }
            return;
        }
        if (target == TargetKind.SqlServer)
        {
            await using var settings = connection.CreateCommand();
            settings.CommandText = "SET QUOTED_IDENTIFIER ON; SET NOCOUNT OFF; SET XACT_ABORT ON;";
            await settings.ExecuteNonQueryAsync(token); return;
        }
        if (target != TargetKind.Sqlite) return;
        await using var command = connection.CreateCommand();
        command.CommandText = (initialize ? "PRAGMA journal_mode=WAL;" : "") + "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(token);
    }
}
