using System.Data.Common;
using BenchForge.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace BenchForge.Worker.CSharp.Databases;

internal static class SqlConnections
{
    public static DbConnection Create(TargetKind target, string connectionString) => target switch
    { TargetKind.SqlServer => new SqlConnection(connectionString), TargetKind.PostgreSql => new NpgsqlConnection(connectionString), TargetKind.Sqlite => new SqliteConnection(connectionString), _ => new MySqlConnection(connectionString) };
    public static string ConnectionString(RunCase run)
    {
        var ep = run.Endpoint;
        return run.Target switch
        {
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
    { TargetKind.Sqlite => "SELECT sqlite_version()", TargetKind.SqlServer => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", _ => "SELECT VERSION()" };

    public static async Task EnsureDatabaseAsync(RunCase run, CancellationToken token)
    {
        if (run.Target != TargetKind.SqlServer) return;
        if (run.Endpoint.Database != "dblab") throw new InvalidDataException("SQL Server ожидает собственную тестовую базу dblab.");
        var builder = new SqlConnectionStringBuilder(ConnectionString(run)) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandTimeout = 60;
        command.CommandText = "IF DB_ID(N'dblab') IS NULL BEGIN CREATE DATABASE [dblab]; END; ALTER DATABASE [dblab] SET RECOVERY SIMPLE; ALTER DATABASE [dblab] SET DELAYED_DURABILITY = DISABLED;";
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task ConfigureAsync(DbConnection connection, TargetKind target, bool initialize, CancellationToken token)
    {
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
