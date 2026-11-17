using System.Net;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using BenchForge.Worker.CSharp;
using BenchForge.Worker.CSharp.Databases;
using Xunit;

namespace BenchForge.Tests;

public sealed class MoreDatabaseTests
{
    [Theory]
    [InlineData(TargetKind.DuckDb)] [InlineData(TargetKind.Oracle)] [InlineData(TargetKind.CockroachDb)]
    [InlineData(TargetKind.Elasticsearch)] [InlineData(TargetKind.OpenSearch)]
    public void NewTargetsRejectUnimplementedLanguageEngines(TargetKind target)
    {
        var config = new BenchmarkConfig { Targets = [target], MemoryMb = Math.Max(2048, Catalog.MinimumMemoryMb(target)) }; config.Validate();
        foreach (var engine in Engines.All.Where(item => item.Id != "csharp"))
            Assert.Throws<ArgumentException>(() => (config with { EngineIds = [engine.Id] }).Validate());
        Assert.Equal(target, JsonSerializer.Deserialize<BenchmarkConfig>(JsonSerializer.Serialize(config, Json.Options), Json.Options)!.Targets[0]);
    }
    [Fact]
    public void FullTextRunsBeforeDeletionAndOnlyForSearchTargets()
    {
        var config = new BenchmarkConfig();
        foreach (var target in new[] { TargetKind.Elasticsearch, TargetKind.OpenSearch })
        {
            var operations = config.Operations(target).ToList();
            Assert.True(operations.IndexOf(Scenario.FullTextSearch) < operations.IndexOf(Scenario.Delete));
        }
        Assert.DoesNotContain(Scenario.FullTextSearch, config.Operations(TargetKind.PostgreSql));
        Assert.True(Catalog.Embedded(TargetKind.DuckDb)); Assert.False(Catalog.Embedded(TargetKind.Oracle));
    }
    [Fact]
    public void FullTextCorpusContainsBoundedPayloadTermsAndChecksWordBoundaries()
    {
        var row = DataGenerator.Create(1, 42, 256); var text = SearchCorpus.Text(row); var term = SearchCorpus.Term(row);
        Assert.True(SearchCorpus.ContainsTerm(text, term.ToLowerInvariant()));
        Assert.DoesNotContain(row.Payload, text);
        Assert.All(text.Split(' '), word => Assert.InRange(word.Length, 1, 16));
        Assert.False(SearchCorpus.ContainsTerm(text, term[1..]));
    }
    [Theory]
    [InlineData(TargetKind.Oracle, "SELECT :id, '@quoted', \"@name\" -- @comment\n WHERE id=:id")]
    [InlineData(TargetKind.DuckDb, "SELECT $id, '@quoted', \"@name\" -- @comment\n WHERE id=$id")]
    public void ParameterMarkersPreserveQuotedSqlAndComments(TargetKind target, string expected)
    {
        Assert.Equal(expected, SqlWorkloadText.BindMarkers("SELECT @id, '@quoted', \"@name\" -- @comment\n WHERE id=@id", target));
        Assert.Equal(new[] { "id" }, SqlWorkloadText.Parameters("SELECT @id, '@quoted' -- @comment\n WHERE id=@id"));
    }
    [Fact]
    public void OracleScriptsPreserveLiteralsAndPlSqlBlocks()
    {
        var parts = SqlScripts.Statements("CREATE TABLE t(id INT); INSERT INTO t VALUES(q'[a;b]'); -- end;\nSELECT ';' FROM dual;", TargetKind.Oracle);
        Assert.Equal(3, parts.Count); Assert.Contains("q'[a;b]'", parts[1]);
        Assert.Single(SqlScripts.Statements("-- test\nBEGIN NULL; NULL; END;\n/", TargetKind.Oracle));
        Assert.Contains("FETCH FIRST", WorkloadTemplates.Store().Operations[1].Steps[0].Command.For(TargetKind.Oracle));
        (new BenchmarkConfig { Targets = [TargetKind.Oracle, TargetKind.CockroachDb, TargetKind.DuckDb], MemoryMb = 2560, CustomWorkload = WorkloadTemplates.Store() }).Validate();
        Assert.Throws<ArgumentException>(() => (new BenchmarkConfig { Targets = [TargetKind.Oracle], MemoryMb = 2048 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkConfig { Targets = [TargetKind.OpenSearch], CustomWorkload = WorkloadTemplates.Store() }).Validate());
        Assert.Equal("SELECT q'[it's @literal]', :key FROM dual", SqlWorkloadText.BindMarkers("SELECT q'[it's @literal]', @key FROM dual", TargetKind.Oracle));
    }
    [Fact]
    public void NativeScalarValuesCanBeCapturedAsPortableParameters()
    {
        var guid = Guid.NewGuid(); Assert.Equal(guid, WorkloadData.ConvertValue(guid.ToByteArray(), SqlValueType.Guid));
        Assert.Equal(900000000000L, WorkloadData.ConvertValue(new System.Numerics.BigInteger(900000000000L), SqlValueType.Int64));
        Assert.Throws<OverflowException>(() => WorkloadData.ConvertValue(System.Numerics.BigInteger.Pow(10, 30), SqlValueType.Int64));
        var stamp = new DateTime(2026, 1, 2, 12, 34, 0, DateTimeKind.Unspecified);
        var captured = Assert.IsType<DateTime>(WorkloadData.ConvertValue(stamp, SqlValueType.DateTime));
        Assert.Equal(DateTimeKind.Utc, captured.Kind); Assert.Equal(12, captured.Hour);
    }
    [Fact]
    public async Task DuckDbExecutesAllStandardOperationsAgainstARealFile()
    {
        using var folder = new TempFolder();
        var config = Config(); var results = await RunAsync(config, folder.Path);
        Assert.Equal(11, results.Count);
        Assert.All(results, result => { Assert.True(result.Verified, result.Phase + " " + JsonSerializer.Serialize(result.ErrorExamples)); Assert.Equal(0, result.Errors); });
    }
    [Fact]
    public async Task DuckDbExecutesStoreTablesJoinsAndTransactions()
    {
        using var folder = new TempFolder(); var config = Config() with { CustomWorkload = WorkloadTemplates.Store() };
        var results = await RunAsync(config, folder.Path);
        Assert.All(results, result => { Assert.True(result.Verified, result.Phase + " " + JsonSerializer.Serialize(result.ErrorExamples)); Assert.Equal(0, result.Errors); });
        Assert.True(results.Single(result => result.Phase == WorkloadPhases.Mix).SuccessfulOperations > 0);
    }
    [Fact]
    public async Task DuckDbPreservesLongTextAndCapturesHugeIntAggregates()
    {
        using var folder = new TempFolder();
        var example = await WorkloadFiles.OpenAsync(Path.Combine(ServerAgentBundle.SourceDirectory(), "configs", "workloads", "large-text.json"), CancellationToken.None);
        var results = await RunAsync(Config() with { CustomWorkload = example.Workload }, folder.Path);
        Assert.All(results, result => { Assert.True(result.Verified, JsonSerializer.Serialize(result.ErrorExamples)); Assert.Equal(0, result.Errors); });
    }
    [Fact]
    public async Task SearchBulkHttpSuccessDoesNotHideAFailedDocument()
    {
        using var folder = new TempFolder(); var config = Config() with { Targets = [TargetKind.Elasticsearch] };
        var run = new RunCase(Guid.NewGuid().ToString("N"), TargetKind.Elasticsearch, 100, 1, 1, config, new("127.0.0.1", 9200, "", "", ""));
        await using var database = new SearchDatabase(run, new PartialBulkHandler());
        await database.InitializeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => database.LoadBatchAsync(DataGenerator.Batch(1, 2, 42, 32), CancellationToken.None));
    }
    private static BenchmarkConfig Config() => new() { Targets = [TargetKind.DuckDb], DatasetRows = [100], Clients = [1], Repeats = 1, DurationSeconds = .05, WarmupSeconds = 0, BatchSize = 20 };
    private static async Task<List<PhaseResult>> RunAsync(BenchmarkConfig config, string folder)
    {
        var run = new RunCase(Guid.NewGuid().ToString("N"), TargetKind.DuckDb, 100, 1, 1, config, new("", 0, "", "", "", Path.Combine(folder, "test.duckdb")));
        using var output = new StringWriter(); await new BenchmarkEngine(run, new ProtocolWriter(output)).RunAsync(CancellationToken.None);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<WireEvent>(line, Json.Options)!)
            .Where(item => item.Type == "result").Select(item => item.Data.Deserialize<PhaseResult>(Json.Options)!).ToList();
    }
    private sealed class PartialBulkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            { "/" => "{\"version\":{\"number\":\"test\"}}", "/_cluster/health" => "{\"timed_out\":false}",
                _ => "{\"errors\":true,\"items\":[{\"create\":{\"status\":201}},{\"create\":{\"status\":400,\"error\":{\"type\":\"mapper_parsing_exception\"}}}]}" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "benchforge-moredb-tests-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
