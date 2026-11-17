using BenchForge.Contracts;

namespace BenchForge.Worker.CSharp.Databases;

public interface IClientSession : IAsyncDisposable
{
    ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token);
}

public interface IDatabase : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken token);
    Task ResetAsync(CancellationToken token);
    Task LoadBatchAsync(DataRow[] rows, CancellationToken token);
    Task PrepareAsync(CancellationToken token);
    Task<DatasetDigest> DigestAsync(CancellationToken token);
    Task<long> CountAsync(CancellationToken token);
    Task<EngineMetadata> MetadataAsync(CancellationToken token);
    Task<IClientSession> OpenSessionAsync(CancellationToken token);
}

public static class DatabaseFactory
{
    public static IDatabase Create(RunCase run) => run.Target switch
    {
        TargetKind.PostgreSql or TargetKind.MySql or TargetKind.MariaDb or TargetKind.Sqlite or TargetKind.SqlServer => new SqlDatabase(run),
        TargetKind.DuckDb or TargetKind.Oracle or TargetKind.CockroachDb => new ExtendedSqlDatabase(run),
        TargetKind.Elasticsearch or TargetKind.OpenSearch => new SearchDatabase(run),
        TargetKind.MongoDb => new MongoDatabase(run),
        TargetKind.Redis or TargetKind.Valkey => new RedisDatabase(run),
        TargetKind.ClickHouse => new ClickHouseDatabase(run),
        _ => throw new NotSupportedException(run.Target.ToString())
    };
}
