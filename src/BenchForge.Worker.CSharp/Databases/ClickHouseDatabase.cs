using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Worker.CSharp.Databases;

// A deliberate HTTP/JSONEachRow transport, recorded in every report.
// This uses the documented ClickHouse HTTP API, not an unlabelled substitute for native TCP.
public sealed class ClickHouseDatabase(RunCase run) : IDatabase
{
    private readonly int _connectionLimit = (int)Math.Min(int.MaxValue, (long)run.Clients + 2);
    private HttpClient _http = null!;
    public async Task InitializeAsync(CancellationToken token)
    {
        _http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = _connectionLimit, AutomaticDecompression = DecompressionMethods.None })
        { BaseAddress = new Uri($"http://{run.Endpoint.Host}:{run.Endpoint.Port}/"), Timeout = TimeSpan.FromMinutes(10) };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(run.Endpoint.Username + ":" + run.Endpoint.Password));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        await QueryAsync("SELECT 1", null, token);
    }
    private async Task<string> QueryAsync(string query, string? body, CancellationToken token)
    {
        var url = "?database=" + Uri.EscapeDataString(run.Endpoint.Database) + "&query=" + Uri.EscapeDataString(query);
        using var content = new StringContent(body ?? "", Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(url, content, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ClickHouse HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 350)]}");
        return text;
    }
    public async Task ResetAsync(CancellationToken token)
    {
        await QueryAsync("DROP TABLE IF EXISTS records", null, token);
        await QueryAsync("CREATE TABLE records (id Int64, group_id Int32, amount_cents Int64, version Int64, payload String) ENGINE=MergeTree ORDER BY id", null, token);
    }
    private static string RowJson(DataRow row) => "{\"id\":" + row.Id.ToString(CultureInfo.InvariantCulture) + ",\"group_id\":" + row.GroupId.ToString(CultureInfo.InvariantCulture)
        + ",\"amount_cents\":" + row.AmountCents.ToString(CultureInfo.InvariantCulture) + ",\"version\":" + row.Version.ToString(CultureInfo.InvariantCulture)
        + ",\"payload\":\"" + row.Payload + "\"}\n";
    public async Task LoadBatchAsync(DataRow[] rows, CancellationToken token)
    {
        var builder = new StringBuilder(); foreach (var row in rows) builder.Append(RowJson(row));
        await QueryAsync("INSERT INTO records FORMAT JSONEachRow", builder.ToString(), token);
    }
    public Task PrepareAsync(CancellationToken token) => Task.CompletedTask; // MergeTree sorting key is part of table creation.
    public async Task<DatasetDigest> DigestAsync(CancellationToken token)
    {
        var text = await QueryAsync("SELECT count(),sum(id),sum(amount_cents) FROM records FORMAT TabSeparated", null, token);
        var fields = text.Trim().Split('\t');
        return new(long.Parse(fields[0], CultureInfo.InvariantCulture), long.Parse(fields[1], CultureInfo.InvariantCulture), long.Parse(fields[2], CultureInfo.InvariantCulture));
    }
    public async Task<long> CountAsync(CancellationToken token) => long.Parse((await QueryAsync("SELECT count() FROM records FORMAT TabSeparated", null, token)).Trim(), CultureInfo.InvariantCulture);
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        var version = (await QueryAsync("SELECT version() FORMAT TabSeparated", null, token)).Trim();
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(typeof(HttpClient)), version,
            new() { ["transport"] = "HTTP / JSONEachRow / uncompressed / .NET HttpClient", ["table"] = "MergeTree ORDER BY id; no unique constraint",
                ["insert"] = "synchronous HTTP insert; async_insert=0 (server default)", ["connections"] = $"HTTP pool max={_connectionLimit}" });
    }
    public Task<IClientSession> OpenSessionAsync(CancellationToken token) => Task.FromResult<IClientSession>(new Session(this, run));
    public ValueTask DisposeAsync() { _http?.Dispose(); return ValueTask.CompletedTask; }
    private sealed class Session(ClickHouseDatabase database, RunCase run) : IClientSession
    {
        public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); token = timeout.Token;
            if (scenario == Scenario.BatchInsert) { await database.LoadBatchAsync(batch!, token); return batch!.Length; }
            var query = scenario switch
            {
                Scenario.RangeRead => $"SELECT id,group_id,amount_cents,version,payload FROM records WHERE id>={key} ORDER BY id LIMIT 100",
                Scenario.Aggregate => $"SELECT group_id,count(),sum(amount_cents) FROM records WHERE group_id={(key - 1) % 100} GROUP BY group_id",
                Scenario.AnalyticalScan => $"SELECT count(),sum(amount_cents),avg(amount_cents) FROM records WHERE id>={key} AND id<{key + Math.Max(100, run.Rows / 100)}",
                _ => throw new NotSupportedException(scenario.ToString())
            };
            // Workload queries use the server-side bound; maintenance has a larger timeout.
            query += " SETTINGS max_execution_time=10 FORMAT JSONEachRow";
            var result = await database.QueryAsync(query, null, token);
            long count = 0;
            foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            { using var document = JsonDocument.Parse(line); _ = document.RootElement.ValueKind; count++; }
            if (count == 0) throw new InvalidDataException("ClickHouse вернул пустой ответ.");
            return count;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
