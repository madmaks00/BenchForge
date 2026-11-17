using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Worker.CSharp.Databases;

public sealed class SearchDatabase(RunCase run, HttpMessageHandler? handler = null) : IDatabase
{
    private HttpClient? _http;
    private string _version = "";
    private const string Index = "records";
    private readonly int _payloadBytes = run.Config.PayloadBytes;
    public async Task InitializeAsync(CancellationToken token)
    {
        var sockets = handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = (int)Math.Min(int.MaxValue, Math.Max(4L, (long)run.Clients + 2)), AllowAutoRedirect = false };
        _http = new HttpClient(sockets) { BaseAddress = new Uri($"http://{run.Endpoint.Host}:{run.Endpoint.Port}/"), Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var info = await RequestAsync(HttpMethod.Get, "", null, token);
            _version = info.RootElement.GetProperty("version").GetProperty("number").GetString()!;
            using var health = await RequestAsync(HttpMethod.Get, "_cluster/health?wait_for_status=yellow&timeout=5s", null, token);
            if (health.RootElement.TryGetProperty("timed_out", out var timeout) && timeout.GetBoolean()) throw new InvalidOperationException("Поисковый узел ещё не готов.");
        }
        catch { _http.Dispose(); _http = null; throw; }
    }
    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, object? body, CancellationToken token, bool maintenance = false, bool allowMissing = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); stop.CancelAfter(TimeSpan.FromSeconds(maintenance ? 600 : 10));
        using var request = new HttpRequestMessage(method, path);
        if (body is string ndjson) request.Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        else if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return JsonDocument.Parse("{}");
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(stop.Token);
            throw new InvalidOperationException($"{Catalog.Get(run.Target).Name}: HTTP {(int)response.StatusCode}: {error[..Math.Min(error.Length, 1000)]}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: stop.Token);
        if (document.RootElement.TryGetProperty("_shards", out var shards) && shards.TryGetProperty("failed", out var failed) && failed.GetInt32() != 0)
        { document.Dispose(); throw new InvalidDataException("Часть поисковых шардов не ответила."); }
        if (document.RootElement.TryGetProperty("timed_out", out var timedOut) && timedOut.GetBoolean())
        { document.Dispose(); throw new TimeoutException("Поисковый запрос вернул неполный результат по тайм-ауту."); }
        return document;
    }
    public async Task ResetAsync(CancellationToken token)
    {
        using (await RequestAsync(HttpMethod.Delete, Index, null, token, true, true)) { }
        using (await RequestAsync(HttpMethod.Put, Index, new
        {
            settings = new Dictionary<string, object> { ["number_of_shards"] = 1, ["number_of_replicas"] = 0, ["refresh_interval"] = "-1", ["translog.durability"] = "request" },
            mappings = new { dynamic = "strict", properties = new Dictionary<string, object>
            {
                ["id"] = new { type = "long" }, ["group_id"] = new { type = "integer" }, ["amount_cents"] = new { type = "long" }, ["version"] = new { type = "long" },
                ["payload"] = new { type = "keyword", index = false, doc_values = false }, ["search_text"] = new { type = "text", analyzer = "standard" }
            } }
        }, token, true)) { }
    }
    private static object Document(DataRow row) => new { id = row.Id, group_id = row.GroupId, amount_cents = row.AmountCents, version = row.Version, payload = row.Payload,
        search_text = SearchCorpus.Text(row) };
    public async Task LoadBatchAsync(DataRow[] rows, CancellationToken token) => await WriteBatchAsync(rows, token, true);
    private async Task WriteBatchAsync(DataRow[] rows, CancellationToken token, bool maintenance = false)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(JsonSerializer.Serialize(new { create = new { _index = Index, _id = row.Id.ToString(CultureInfo.InvariantCulture) } }, Json.Options)).Append('\n');
            text.Append(JsonSerializer.Serialize(Document(row), Json.Options)).Append('\n');
        }
        using var result = await RequestAsync(HttpMethod.Post, "_bulk?refresh=false", text.ToString(), token, maintenance);
        if (!result.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() != rows.Length) throw new InvalidDataException("Bulk вернул неверное число результатов.");
        foreach (var item in items.EnumerateArray())
        {
            var action = item.GetProperty("create");
            if (action.GetProperty("status").GetInt32() != 201 || action.TryGetProperty("error", out _))
                throw new InvalidDataException("Ошибка отдельного документа bulk: " + action.ToString()[..Math.Min(action.ToString().Length, 500)]);
        }
        if (result.RootElement.TryGetProperty("errors", out var errors) && errors.GetBoolean()) throw new InvalidDataException("Bulk сообщил ошибки документов.");
    }
    private async Task RefreshAsync(CancellationToken token) { using var refresh = await RequestAsync(HttpMethod.Post, Index + "/_refresh", null, token, true); }
    public async Task PrepareAsync(CancellationToken token)
    {
        using (await RequestAsync(HttpMethod.Put, Index + "/_settings", new { index = new { refresh_interval = "1s" } }, token, true)) { }
        await RefreshAsync(token);
    }
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        await RefreshAsync(token);
        using var result = await RequestAsync(HttpMethod.Post, Index + "/_search", new { size = 0, track_total_hits = true,
            aggs = new { ids = new { sum = new { field = "id" } }, amounts = new { sum = new { field = "amount_cents" } } } }, token, true);
        var body = result.RootElement;
        return new(body.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64(),
            checked((long)body.GetProperty("aggregations").GetProperty("ids").GetProperty("value").GetDouble()),
            checked((long)body.GetProperty("aggregations").GetProperty("amounts").GetProperty("value").GetDouble()));
    }
    public async Task<long> CountAsync(CancellationToken token)
    {
        await RefreshAsync(token); using var count = await RequestAsync(HttpMethod.Get, Index + "/_count", null, token, true);
        return count.RootElement.GetProperty("count").GetInt64();
    }
    public Task<EngineMetadata> MetadataAsync(CancellationToken token) => Task.FromResult(new EngineMetadata("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(typeof(HttpClient)), _version,
        new() { ["transport"] = "HTTP JSON / NDJSON bulk; no client retries", ["topology"] = "single node, 1 primary shard, 0 replicas; search profile",
            ["durability"] = "translog.durability=request; writes acknowledged before scheduled search refresh", ["refresh"] = "disabled during initial load; 1s during tests; explicit refresh for digest/count outside request timing",
            ["bulk_atomicity"] = "not atomic; each item checked; partial writes cause count mismatch and failed verification",
            ["search_text"] = SearchCorpus.Version + "; payload split into 16-character terms; standard analyzer; match first payload term, fetch up to 10 documents; term regeneration inside measured call",
            ["security"] = "disabled only in owned loopback test container; remote control uses SSH", ["generator"] = DataGenerator.Version,
            ["store"] = "node.store.allow_mmap=false; no host sysctl changes; NIO test profile",
            ["connections"] = "shared HTTP pool; one request at a time per logical client" }));
    public Task<IClientSession> OpenSessionAsync(CancellationToken token) => Task.FromResult<IClientSession>(new Session(this, run));
    public ValueTask DisposeAsync() { _http?.Dispose(); return ValueTask.CompletedTask; }
    private sealed class Session(SearchDatabase database, RunCase run) : IClientSession
    {
        public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
        {
            var id = key.ToString(CultureInfo.InvariantCulture);
            if (scenario == Scenario.BatchInsert) { await database.WriteBatchAsync(batch!, token); return batch!.Length; }
            if (scenario == Scenario.PointRead)
            {
                using var result = await database.RequestAsync(HttpMethod.Get, Index + "/_doc/" + id, null, token);
                var source = result.RootElement.GetProperty("_source");
                if (source.GetProperty("id").GetInt64() != key || !result.RootElement.GetProperty("found").GetBoolean()) throw new InvalidDataException("Неверный ID в документе.");
                if (source.GetProperty("payload").GetString()?.Length != database._payloadBytes) throw new InvalidDataException("Неверная длина текстового поля."); return 1;
            }
            if (scenario == Scenario.Insert)
            {
                using var result = await database.RequestAsync(HttpMethod.Put, Index + "/_create/" + row!.Id.ToString(CultureInfo.InvariantCulture) + "?refresh=false", Document(row), token);
                if (result.RootElement.GetProperty("result").GetString() != "created") throw new InvalidDataException("Документ не создан."); return 1;
            }
            if (scenario == Scenario.Update)
            {
                using var result = await database.RequestAsync(HttpMethod.Post, Index + "/_update/" + id + "?refresh=false&retry_on_conflict=0", new { script = new { source = "ctx._source.version += 1", lang = "painless" } }, token);
                if (result.RootElement.GetProperty("result").GetString() != "updated") throw new InvalidDataException("Версия не обновилась."); return 1;
            }
            if (scenario == Scenario.Delete)
            {
                using var result = await database.RequestAsync(HttpMethod.Delete, Index + "/_doc/" + id + "?refresh=false", null, token);
                if (result.RootElement.GetProperty("result").GetString() != "deleted") throw new InvalidDataException("Документ не удалён."); return 1;
            }
            var group = (int)((key - 1) % 100);
            if (scenario == Scenario.Aggregate)
            {
                using var result = await database.RequestAsync(HttpMethod.Post, Index + "/_search", new { size = 0, track_total_hits = true,
                    query = new { term = new { group_id = group } }, aggs = new { amount = new { sum = new { field = "amount_cents" } } } }, token);
                if (result.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64() < 1) throw new InvalidDataException("Группа пуста.");
                _ = result.RootElement.GetProperty("aggregations").GetProperty("amount").GetProperty("value").GetDouble(); return 1;
            }
            var term = scenario == Scenario.FullTextSearch ? SearchCorpus.Term(DataGenerator.Create(key, run.Config.Seed, run.Config.PayloadBytes)) : "";
            object body = scenario switch
            {
                Scenario.RangeRead => new { size = 100, query = new { range = new { id = new { gte = key } } }, sort = new[] { new { id = "asc" } } },
                Scenario.FullTextSearch => new { size = 10, query = new { match = new { search_text = term } } },
                _ => throw new NotSupportedException(scenario.ToString())
            };
            using var found = await database.RequestAsync(HttpMethod.Post, Index + "/_search", body, token);
            var hits = found.RootElement.GetProperty("hits").GetProperty("hits");
            if (hits.GetArrayLength() == 0) throw new InvalidDataException("Поиск вернул пустой ответ.");
            long previous = key - 1;
            foreach (var hit in hits.EnumerateArray())
            {
                var source = hit.GetProperty("_source"); var actual = source.GetProperty("id").GetInt64(); _ = source.GetProperty("payload").GetString();
                if (scenario == Scenario.RangeRead && actual <= previous) throw new InvalidDataException("Нарушен порядок диапазона.");
                if (scenario == Scenario.FullTextSearch && !SearchCorpus.ContainsTerm(source.GetProperty("search_text").GetString()!, term)) throw new InvalidDataException("Документ не содержит искомого термина.");
                previous = actual;
            }
            return hits.GetArrayLength();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
