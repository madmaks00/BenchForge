using System.Globalization;
using System.Runtime.InteropServices;
using BenchForge.Contracts;
using StackExchange.Redis;

namespace BenchForge.Worker.CSharp.Databases;

public sealed class RedisDatabase(RunCase run) : IDatabase
{
    private ConnectionMultiplexer _connection = null!;
    private IDatabaseAsync _database = null!;
    private IServer _server = null!;
    private readonly string _prefix = "dblab:" + run.RunId + ":";
    public async Task InitializeAsync(CancellationToken token)
    {
        var options = new ConfigurationOptions { AbortOnConnectFail = true, ConnectTimeout = 5000, ConnectRetry = 0,
            AsyncTimeout = 10000, SyncTimeout = 10000, Password = run.Endpoint.Password, AllowAdmin = true };
        options.EndPoints.Add(run.Endpoint.Host, run.Endpoint.Port);
        _connection = await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(token);
        _database = _connection.GetDatabase(0); _server = _connection.GetServer(_connection.GetEndPoints()[0]);
        await _database.PingAsync();
    }
    public Task ResetAsync(CancellationToken token) => Task.CompletedTask; // New owned instance and unique namespace per case.
    internal static HashEntry[] Entries(DataRow row) => [new("group_id", row.GroupId), new("amount_cents", row.AmountCents),
        new("version", row.Version), new("payload", row.Payload)];
    private RedisKey Key(long id) => _prefix + id.ToString(CultureInfo.InvariantCulture);
    public Task LoadBatchAsync(DataRow[] rows, CancellationToken token) => Task.WhenAll(rows.Select(row => _database.HashSetAsync(Key(row.Id), Entries(row))));
    public Task PrepareAsync(CancellationToken token) => Task.CompletedTask;
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        // Bounded scan inside each Lua invocation. Initial sums stay below 2^53.
        const string script = "local r=redis.call('SCAN',ARGV[1],'MATCH',ARGV[2]..'*','COUNT',1000); local n=0; local ids=0; local a=0; for _,k in ipairs(r[2]) do n=n+1; ids=ids+tonumber(string.sub(k,string.len(ARGV[2])+1)); a=a+tonumber(redis.call('HGET',k,'amount_cents')); end; return {r[1],n,ids,a}";
        long cursor = 0, count = 0, ids = 0, amounts = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var values = (RedisResult[])(await _database.ScriptEvaluateAsync(script, values: [cursor, _prefix]))!;
            cursor = long.Parse(values[0].ToString(), CultureInfo.InvariantCulture);
            count += (long)values[1]; ids += (long)values[2]; amounts += (long)values[3];
        } while (cursor != 0);
        return new(count, ids, amounts);
    }
    public async Task<long> CountAsync(CancellationToken token)
    {
        // Only this benchmark owns this ephemeral server; no metadata keys are created.
        return await _server.DatabaseSizeAsync(0);
    }
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        var info = await _server.InfoAsync("server");
        var fields = info.SelectMany(x => x).ToDictionary(x => x.Key, x => x.Value);
        var version = fields.GetValueOrDefault("valkey_version", fields.GetValueOrDefault("redis_version", _server.Version.ToString()));
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(typeof(ConnectionMultiplexer)), version,
            new() { ["connections"] = "one shared multiplexed connection; clients are logical operations, not sockets",
                ["storage"] = "hash per record; HGETALL / HINCRBY / HSET / DEL", ["batch_transport"] = "pipelined HSET, non-atomic batch",
                ["durability_profile"] = "memory cache: AOF and RDB disabled; not compared as durable SQL writes" });
    }
    public Task<IClientSession> OpenSessionAsync(CancellationToken token) => Task.FromResult<IClientSession>(new Session(_database, _prefix));
    public async ValueTask DisposeAsync() { if (_connection is not null) await _connection.DisposeAsync(); }

    private sealed class Session(IDatabaseAsync database, string prefix) : IClientSession
    {
        private RedisKey Key(long id) => prefix + id.ToString(CultureInfo.InvariantCulture);
        public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
        {
            // Once dispatched, await completion instead of abandoning a possibly committed write.
            switch (scenario)
            {
                case Scenario.PointRead:
                    var fields = await database.HashGetAllAsync(Key(key));
                    if (fields.Length != 4) throw new InvalidDataException("Redis/Valkey вернул неполную запись."); return 1;
                case Scenario.Update: await database.HashIncrementAsync(Key(key), "version", 1); return 1;
                case Scenario.Insert: await database.HashSetAsync(Key(row!.Id), Entries(row)); return 1;
                case Scenario.BatchInsert:
                    await Task.WhenAll(batch!.Select(r => database.HashSetAsync(Key(r.Id), Entries(r)))); return batch!.Length;
                case Scenario.Delete:
                    if (!await database.KeyDeleteAsync(Key(key))) throw new InvalidDataException("Redis/Valkey не нашёл удаляемый ключ."); return 1;
                default: throw new NotSupportedException(scenario.ToString());
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
