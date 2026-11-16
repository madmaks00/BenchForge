using System.Runtime.InteropServices;
using BenchForge.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BenchForge.Worker.CSharp.Databases;

public sealed class MongoDatabase(RunCase run) : IDatabase
{
    private MongoClient _client = null!;
    private IMongoDatabase _db = null!;
    private IMongoCollection<BsonDocument> Collection => _db.GetCollection<BsonDocument>("records");
    public async Task InitializeAsync(CancellationToken token)
    {
        var ep = run.Endpoint;
        _client = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress(ep.Host, ep.Port), Credential = MongoCredential.CreateCredential("admin", ep.Username, ep.Password),
            ConnectTimeout = TimeSpan.FromSeconds(5), ServerSelectionTimeout = TimeSpan.FromSeconds(5), SocketTimeout = TimeSpan.FromMinutes(10),
            RetryWrites = false, MaxConnectionPoolSize = (int)Math.Max(4L, Math.Min(int.MaxValue, (long)run.Clients + 2)),
            WriteConcern = WriteConcern.W1.With(journal: true, wTimeout: TimeSpan.FromSeconds(10))
        });
        _db = _client.GetDatabase(ep.Database);
        await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: token);
    }
    public async Task ResetAsync(CancellationToken token)
    { await _db.DropCollectionAsync("records", token); await _db.CreateCollectionAsync("records", cancellationToken: token); }
    internal static BsonDocument Document(DataRow row) => new() { ["_id"] = row.Id, ["group_id"] = row.GroupId,
        ["amount_cents"] = row.AmountCents, ["version"] = row.Version, ["payload"] = row.Payload };
    public Task LoadBatchAsync(DataRow[] rows, CancellationToken token) => Collection.InsertManyAsync(rows.Select(Document), cancellationToken: token);
    public async Task PrepareAsync(CancellationToken token) => await Collection.Indexes.CreateOneAsync(
        new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("group_id")), cancellationToken: token);
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        var pipeline = new[] { new BsonDocument("$group", new BsonDocument { ["_id"] = BsonNull.Value, ["n"] = new BsonDocument("$sum", 1),
            ["ids"] = new BsonDocument("$sum", "$_id"), ["amount"] = new BsonDocument("$sum", "$amount_cents") }) };
        var result = await Collection.Aggregate<BsonDocument>(pipeline, new AggregateOptions { MaxTime = TimeSpan.FromMinutes(10) }).FirstOrDefaultAsync(token);
        return result is null ? new(0, 0, 0) : new(result["n"].ToInt64(), result["ids"].ToInt64(), result["amount"].ToInt64());
    }
    public Task<long> CountAsync(CancellationToken token) => Collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token);
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        var build = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: token);
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(typeof(MongoClient)), build["version"].AsString,
            new() { ["write_concern"] = "w=1,j=true", ["retry_writes"] = "false", ["topology"] = "standalone",
                ["connections"] = $"shared driver pool, max={_client.Settings.MaxConnectionPoolSize}; clients are logical operations" });
    }
    public Task<IClientSession> OpenSessionAsync(CancellationToken token) => Task.FromResult<IClientSession>(new Session(Collection));
    public ValueTask DisposeAsync() { _client?.Dispose(); return ValueTask.CompletedTask; }

    private sealed class Session(IMongoCollection<BsonDocument> collection) : IClientSession
    {
        public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); token = timeout.Token;
            var filter = Builders<BsonDocument>.Filter.Eq("_id", key);
            switch (scenario)
            {
                case Scenario.PointRead:
                    var document = await collection.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(10) }).FirstOrDefaultAsync(token);
                    if (document is null || document["_id"].ToInt64() != key) throw new InvalidDataException("Неверный результат чтения MongoDB.");
                    _ = document["payload"].AsString; return 1;
                case Scenario.RangeRead:
                    var rows = await collection.Find(Builders<BsonDocument>.Filter.Gte("_id", key), new FindOptions { MaxTime = TimeSpan.FromSeconds(10) })
                        .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).Limit(100).ToListAsync(token);
                    if (rows.Count == 0) throw new InvalidDataException("Пустой диапазон MongoDB."); return rows.Count;
                case Scenario.Aggregate:
                    var pipeline = new[] { new BsonDocument("$match", new BsonDocument("group_id", (key - 1) % 100)),
                        new BsonDocument("$group", new BsonDocument { ["_id"] = "$group_id", ["n"] = new BsonDocument("$sum", 1), ["amount"] = new BsonDocument("$sum", "$amount_cents") }) };
                    var aggregate = await collection.Aggregate<BsonDocument>(pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(10) }).ToListAsync(token);
                    if (aggregate.Count == 0) throw new InvalidDataException("Пустая агрегация MongoDB."); return aggregate.Count;
                case Scenario.Update:
                    if ((await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Inc("version", 1L), cancellationToken: token)).MatchedCount != 1)
                        throw new InvalidDataException("Обновление MongoDB не нашло запись."); return 1;
                case Scenario.Insert: await collection.InsertOneAsync(Document(row!), cancellationToken: token); return 1;
                case Scenario.BatchInsert: await collection.InsertManyAsync(batch!.Select(Document), cancellationToken: token); return batch!.Length;
                case Scenario.Delete:
                    if ((await collection.DeleteOneAsync(filter, token)).DeletedCount != 1) throw new InvalidDataException("Удаление MongoDB не нашло запись."); return 1;
                default: throw new NotSupportedException(scenario.ToString());
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
