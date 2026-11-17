using System.Data.Common;
using BenchForge.Contracts;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace BenchForge.Worker.CSharp.Databases;

internal static class SqlConnections
{
    public static DbConnection Create(TargetKind target, string connectionString) => target switch
    { TargetKind.PostgreSql => new NpgsqlConnection(connectionString), TargetKind.Sqlite => new SqliteConnection(connectionString), _ => new MySqlConnection(connectionString) };
    public static string ConnectionString(RunCase run)
    {
        var ep = run.Endpoint;
        return run.Target switch
        {
            TargetKind.PostgreSql => new NpgsqlConnectionStringBuilder { Host = ep.Host, Port = ep.Port, Username = ep.Username, Password = ep.Password,
                Database = ep.Database, Pooling = false, Timeout = 5, CommandTimeout = 10, Options = "-c synchronous_commit=on" }.ConnectionString,
            TargetKind.Sqlite => new SqliteConnectionStringBuilder { DataSource = ep.FilePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ConnectionString,
            _ => new MySqlConnectionStringBuilder { Server = ep.Host, Port = (uint)ep.Port, UserID = ep.Username, Password = ep.Password,
                Database = ep.Database, Pooling = false, ConnectionTimeout = 5, DefaultCommandTimeout = 10, AllowPublicKeyRetrieval = true, SslMode = MySqlSslMode.Disabled }.ConnectionString
        };
    }
    public static async Task ConfigureAsync(DbConnection connection, TargetKind target, bool initialize, CancellationToken token)
    {
        if (target != TargetKind.Sqlite) return;
        await using var command = connection.CreateCommand();
        command.CommandText = (initialize ? "PRAGMA journal_mode=WAL;" : "") + "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(token);
    }
}
