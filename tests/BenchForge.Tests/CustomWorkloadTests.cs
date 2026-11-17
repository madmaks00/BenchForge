using System.Data;
using System.IO.Compression;
using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using BenchForge.Worker.CSharp;
using BenchForge.Worker.CSharp.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BenchForge.Tests;

public sealed class CustomWorkloadTests
{
    private static BenchmarkConfig Config(SqlWorkload workload) => new() { CustomWorkload = workload, DatasetRows = [100], Clients = [2], Repeats = 1,
        DurationSeconds = .1, WarmupSeconds = .05, BatchSize = 50 };

    [Theory]
    [InlineData("store")]
    [InlineData("wallets")]
    [InlineData("blank")]
    public async Task TemplatesExecuteRealSqliteQueriesAndCheckIntegrity(string template)
    {
        using var folder = new Folder();
        var workload = template switch { "store" => WorkloadTemplates.Store(), "wallets" => WorkloadTemplates.Wallets(), _ => WorkloadTemplates.Blank() };
        var messages = await RunAsync(Config(workload), folder.Path);
        var results = Results(messages);
        Assert.All(results, result => { Assert.True(result.Verified); Assert.Equal(0, result.Errors); });
        var mix = Assert.Single(results, result => result.Phase == WorkloadPhases.Mix);
        Assert.True(mix.SuccessfulOperations > 0);
        Assert.Equal(mix.SuccessfulOperations, mix.OperationBreakdown!.Sum(operation => operation.SuccessfulOperations));
        Assert.Equal(mix.ServiceLatency!.Count, mix.OperationBreakdown!.Sum(operation => operation.ServiceLatency.Count));
    }

    [Fact]
    public async Task SeparateQueriesAndMixKeepTheirOwnPhasesAndGraphStages()
    {
        using var folder = new Folder(); var workload = WorkloadTemplates.Wallets() with { Mode = WorkloadMode.SeparateAndMix };
        var config = Config(workload) with { Plan = LoadPlan.Spike(1, 2, .05, .05, .05) };
        var rows = Results(await RunAsync(config, folder.Path));
        var measured = rows.Where(row => !ReportAnalysis.SetupPhase(row.Phase)).ToArray();
        Assert.Equal(9, measured.Length);
        Assert.Equal(new[] { 1, 2, 1, 1, 2, 1, 1, 2, 1 }, measured.Select(row => row.Clients));
        Assert.Equal(3, measured.Select(row => row.Phase).Distinct().Count());
        Assert.All(measured, row => { Assert.True(row.Verified); Assert.Equal(0, row.Errors); });
        Assert.Equal(3, rows.Count(row => row.Phase.EndsWith("/load")));
    }

    [Fact]
    public async Task ConstantRateAccountsForEveryScheduledCustomOperation()
    {
        using var folder = new Folder(); var config = Config(WorkloadTemplates.Blank()) with { Plan = LoadPlan.Single(LoadProfile.FixedRate, 2, .1, 100000), WarmupSeconds = 0 };
        var result = Assert.Single(Results(await RunAsync(config, folder.Path)), row => row.Phase == WorkloadPhases.Mix);
        Assert.Equal(10000, result.PlannedOperations);
        Assert.Equal(result.PlannedOperations, result.SuccessfulOperations + result.Errors + result.Dropped);
        Assert.True(result.Dropped > 0);
    }

    [Fact]
    public async Task FailedStepRollsBackTheWholeBusinessTransaction()
    {
        using var folder = new Folder(); var original = WorkloadTemplates.Wallets();
        var transfer = original.Operations[1] with { Steps = [original.Operations[1].Steps[0], new() { Command = new() { Sql = "SELECT id FROM wallets WHERE id=-1" }, MinRows = 1 }] };
        var config = Config(original with { Operations = [transfer] });
        var run = Case(config, folder.Path); await using var database = new CustomSqlDatabase(run);
        await database.InitializeAsync(CancellationToken.None); await database.ResetAsync(CancellationToken.None); await database.LoadAsync(null, CancellationToken.None);
        await using var session = await database.OpenSessionAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ExecuteAsync(Scenario.PointRead, 1, null, null, CancellationToken.None).AsTask());
        await database.CheckAsync(false, CancellationToken.None);
    }

    [Fact]
    public async Task ScalarOutputCanBecomeParameterOfNextStep()
    {
        using var folder = new Folder(); var workload = WorkloadTemplates.Blank() with
        {
            Operations = [new() { Id = "captured", Name = "Captured value", Transaction = true, Steps =
                [new() { Command = new() { Sql = "SELECT @key" }, Result = SqlResultMode.Scalar, CaptureAs = "chosen", MinRows = 1 },
                 new() { Command = new() { Sql = "SELECT id FROM items WHERE id=@chosen" }, MinRows = 1, MaxRows = 1 }] }]
        };
        var measured = Assert.Single(Results(await RunAsync(Config(workload), folder.Path)), row => row.Phase == WorkloadPhases.Mix);
        Assert.True(measured.Verified); Assert.Equal(0, measured.Errors);
    }

    [Fact]
    public async Task ImportedCsvPreservesQuotedUnicodeAndMultilineFields()
    {
        using var folder = new Folder(); var file = folder.Path + "/items.csv";
        await File.WriteAllTextAsync(file, "id,title\r\n1,\"Привет, мир\"\r\n2,\"две\nстроки и \"\"кавычки\"\"\"\r\n");
        var inspection = await WorkloadFiles.InspectAsync(file, ImportFormat.Csv, CancellationToken.None);
        Assert.Equal(2, inspection.Rows);
        var workload = WorkloadTemplates.Blank();
        var table = workload.Datasets[0] with { Import = new("items.csv", ImportFormat.Csv, inspection.Sha256, inspection.Rows) };
        workload = workload with { Datasets = [table], Operations = [workload.Operations[0] with { Parameters = [new() { Name = "id", Generator = ValueGenerator.Reference, Reference = "items" }],
            Steps = [new() { Command = new() { Sql = "SELECT id,title FROM items WHERE id=@id" }, MinRows = 1, MaxRows = 1 }] }] };
        var config = Config(workload) with { WorkloadDirectory = folder.Path };
        Assert.All(Results(await RunAsync(config, folder.Path)), row => Assert.True(row.Verified));
        await using var connection = new SqliteConnection("Data Source=" + folder.Path + "/test.sqlite3;Pooling=False"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT title FROM items WHERE id=2";
        Assert.Equal("две\nстроки и \"кавычки\"", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ImportedJsonLinesAndPackagesRoundTripWithoutEmbeddingDataInReportConfig()
    {
        using var folder = new Folder(); await File.WriteAllTextAsync(folder.Path + "/items.jsonl", "{\"id\":1,\"title\":\"пример\"}\n{\"id\":2,\"title\":\"строка\"}\n");
        var inspection = await WorkloadFiles.InspectAsync(folder.Path + "/items.jsonl", ImportFormat.JsonLines, CancellationToken.None);
        var original = WorkloadTemplates.Blank(); var workload = original with { Datasets = [original.Datasets[0] with { Import = new("items.jsonl", ImportFormat.JsonLines, inspection.Sha256, 2) }] };
        var package = folder.Path + "/sample.bfworkload";
        await WorkloadFiles.SavePackageAsync(workload, folder.Path, package, CancellationToken.None);
        var loaded = await WorkloadFiles.OpenAsync(package, CancellationToken.None);
        Assert.Equal(workload.Fingerprint, loaded.Workload.Fingerprint);
        await WorkloadFiles.CheckAsync(Config(loaded.Workload) with { WorkloadDirectory = loaded.Directory }, CancellationToken.None);
        Assert.DoesNotContain("пример", JsonSerializer.Serialize(Config(workload), Json.Options));
    }

    [Fact]
    public async Task ModifiedInputFileIsRejectedBeforeAnyDatabaseStarts()
    {
        using var folder = new Folder(); var path = folder.Path + "/items.csv"; await File.WriteAllTextAsync(path, "id,title\n1,a\n");
        var inspection = await WorkloadFiles.InspectAsync(path, ImportFormat.Csv, CancellationToken.None);
        var workload = WorkloadTemplates.Blank(); workload = workload with { Datasets = [workload.Datasets[0] with { Import = new("items.csv", ImportFormat.Csv, inspection.Sha256, 1) }] };
        await File.AppendAllTextAsync(path, "2,b\n");
        var problems = await ExperimentRunner.PreflightAsync(Config(workload) with { WorkloadDirectory = folder.Path }, CancellationToken.None);
        Assert.Contains(problems, message => message.Contains("изменён"));
    }

    [Fact]
    public async Task UnsafePackageEntryIsRejected()
    {
        using var folder = new Folder(); var path = folder.Path + "/unsafe.bfworkload";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("workload.json").Open())) writer.Write(JsonSerializer.Serialize(WorkloadTemplates.Blank(), Json.Options));
            using (var writer = new StreamWriter(zip.CreateEntry("../outside").Open())) writer.Write("bad");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkloadFiles.OpenAsync(path, CancellationToken.None));
        Assert.False(File.Exists(folder.Path + "/outside"));
    }

    [Fact]
    public void CustomWorkloadsRejectUnsupportedTargetsAndEngines()
    {
        Assert.Throws<ArgumentException>(() => (Config(WorkloadTemplates.Blank()) with { Targets = [TargetKind.MongoDb] }).Validate());
        Assert.Throws<ArgumentException>(() => (Config(WorkloadTemplates.Blank()) with { EngineIds = ["go"] }).Validate());
    }

    [Fact]
    public void ParameterLexerIgnoresStringsCommentsAndSystemVariables() =>
        Assert.Equal(new[] { "id", "name" }, SqlWorkloadText.Parameters("SELECT '@ignored', \"@column\", $$ @dollar $$, @@version, @id /* @x /* @nested */ */ -- @line\n WHERE name=@name AND id=@id"));

    [Fact]
    public async Task CsvSemicolonDelimiterIsDetectedAndExportDoesNotOverwriteForeignFiles()
    {
        using var folder = new Folder(); var file = folder.Path + "/items.csv";
        await File.WriteAllTextAsync(file, "id;title\n1;\"one;two\"\n");
        var inspected = await WorkloadFiles.InspectAsync(file, ImportFormat.Csv, CancellationToken.None);
        Assert.Equal(';', inspected.Delimiter); Assert.Equal(1, inspected.Rows);
        Assert.Equal("one;two", Assert.Single(WorkloadData.ReadFile(file, ImportFormat.Csv, CancellationToken.None, inspected.Delimiter))["title"]);
        var workload = WorkloadTemplates.Blank(); workload = workload with { Datasets = [workload.Datasets[0] with { Import = new("items.csv", ImportFormat.Csv, inspected.Sha256, 1, ';') }] };
        var config = Config(workload) with { WorkloadDirectory = folder.Path };
        Directory.CreateDirectory(folder.Path + "/export"); await File.WriteAllTextAsync(folder.Path + "/export/items.csv", "foreign");
        await Assert.ThrowsAsync<IOException>(() => WorkloadFiles.CopyInputsAsync(config, folder.Path + "/export", CancellationToken.None));
        Assert.Equal("foreign", await File.ReadAllTextAsync(folder.Path + "/export/items.csv"));
    }

    [Fact]
    public void UnknownParametersAndCapturesUsedBeforeTheirStepAreRejected()
    {
        var workload = WorkloadTemplates.Blank();
        workload = workload with { Operations = [workload.Operations[0] with { Steps = [new() { Command = new() { Sql = "SELECT @unknown" } }] }] };
        Assert.Throws<ArgumentException>(() => Config(workload).Validate());
    }

    [Fact]
    public async Task CustomCancellationSavesPartialResultsAndCleansOwnedSqlite()
    {
        using var folder = new Folder(); using var stop = new CancellationTokenSource();
        var config = Config(WorkloadTemplates.Wallets()) with { DurationSeconds = 10, WarmupSeconds = 0 };
        var result = await new ExperimentRunner(update => { if (update.Type == "log" && update.Message.EndsWith(" · custom/mix")) stop.CancelAfter(150); })
            .RunAsync(config, folder.Path, stop.Token);
        Assert.Equal("cancelled", result.Report.Status);
        Assert.Contains(result.Report.Results, row => row.Phase == WorkloadPhases.Mix && row.Cancelled);
        Assert.Empty(Directory.EnumerateFiles(result.Folder, "*.sqlite3*"));
        Assert.True(File.Exists(result.Folder + "/operations.csv"));
    }

    [Fact]
    public async Task SampleUsesActualImportedStringKeysWithoutNumericIdAssumptions()
    {
        using var folder = new Folder(); var path = folder.Path + "/items.jsonl";
        await File.WriteAllTextAsync(path, "{\"id\":\"a-001\",\"title\":\"first\"}\n{\"id\":\"another-key\",\"title\":\"second\"}\n");
        var inspected = await WorkloadFiles.InspectAsync(path, ImportFormat.JsonLines, CancellationToken.None);
        var workload = WorkloadTemplates.Blank() with
        {
            Datasets = [new() { Name = "items", Import = new("items.jsonl", ImportFormat.JsonLines, inspected.Sha256, 2), Columns =
                [new() { Name = "id", Type = SqlValueType.Text, Generator = ValueGenerator.Text, PrimaryKey = true }, new() { Name = "title", Type = SqlValueType.Text, Generator = ValueGenerator.Text }] }],
            Operations = [new() { Id = "by_key", Name = "By real string key", Parameters = [new() { Name = "id", Type = SqlValueType.Text, Generator = ValueGenerator.Sample, Reference = "items", SampleColumn = "id", SampleSize = 1 }],
                Steps = [new() { Command = new() { Sql = "SELECT title FROM items WHERE id=@id" }, MinRows = 1, MaxRows = 1 }] }]
        };
        Assert.All(Results(await RunAsync(Config(workload) with { WorkloadDirectory = folder.Path }, folder.Path)), row => { Assert.True(row.Verified); Assert.Equal(0, row.Errors); });
    }

    [Fact]
    public void DecimalGeneratorSupportsFullSigned64BitRangeWithoutIntermediateOverflow()
    {
        var field = new ValueDefinition { Type = SqlValueType.Decimal, Generator = ValueGenerator.Decimal, Min = long.MinValue, Max = long.MaxValue, Scale = 12 };
        var random = new PortableRandom(42);
        for (var index = 0; index < 100; index++) Assert.InRange((decimal)WorkloadData.Generate(field, new(1, 1, 1, 1, 100, new Dictionary<string, long>()), ref random), long.MinValue, long.MaxValue);
    }

    [Fact]
    public void CustomPhaseNamesAndDurationDirectionWorkInReports()
    {
        var workload = WorkloadTemplates.Store();
        Assert.Equal("Создание заказа", ReportAnalysis.PhaseName(WorkloadPhases.Query("checkout"), workload));
        Assert.True(ReportAnalysis.SetupPhase("custom/setup/mix/schema"));
        Assert.Equal(MetricDirection.Lower, ReportAnalysis.Describe(AnalysisMetric.Rate, "custom/setup/mix/schema").Direction);
        Assert.Equal("зап./с", ReportAnalysis.RateUnit("custom/setup/mix/load"));
        var row = new PhaseResult { Phase = WorkloadPhases.Mix, Target = TargetKind.Sqlite, DatasetRows = 100, Clients = 2, Repeat = 1, Verified = true, OperationsPerSecond = 10 };
        var report = new BenchmarkReport { Config = Config(workload), Results = [row] };
        Assert.Equal(10, Assert.Single(ReportAnalysis.Compare(report, report.Results, new(WorkloadPhases.Mix, 100, 2), AnalysisMetric.Rate)).Median);
    }

    private static RunCase Case(BenchmarkConfig config, string folder) => new(Guid.NewGuid().ToString("N"), TargetKind.Sqlite, 100, config.CaseClients().Max(), 1, config,
        new("", 0, "", "", "", folder + "/test.sqlite3"), InputDirectory: config.WorkloadDirectory);
    private static async Task<List<WireEvent>> RunAsync(BenchmarkConfig config, string folder)
    {
        config.Validate(); using var output = new StringWriter(); await new BenchmarkEngine(Case(config, folder), new ProtocolWriter(output)).RunAsync(CancellationToken.None);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<WireEvent>(line, Json.Options)!).ToList();
    }
    private static List<PhaseResult> Results(List<WireEvent> messages) => messages.Where(message => message.Type == "result").Select(message => message.Data.Deserialize<PhaseResult>(Json.Options)!).ToList();
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "benchforge-custom-test-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
