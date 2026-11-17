using System.Text.Json;
using BenchForge.Contracts;
using Xunit;

namespace BenchForge.Tests;

public sealed class SqlServerTests
{
    [Fact]
    public void ExistingTargetNumbersStayStableAndSqlServerRoundTrips()
    {
        Assert.Equal(7, (int)TargetKind.Sqlite);
        var config = new BenchmarkConfig { Targets = [TargetKind.SqlServer] };
        config.Validate();
        var restored = JsonSerializer.Deserialize<BenchmarkConfig>(JsonSerializer.Serialize(config, Json.Options), Json.Options)!;
        Assert.Equal(TargetKind.SqlServer, Assert.Single(restored.Targets));
        Assert.Contains(Scenario.BatchInsert, restored.Operations(TargetKind.SqlServer));
        Assert.Equal(1433, Catalog.Get(TargetKind.SqlServer).Port);
    }

    [Fact]
    public void UnsupportedLanguagesAndInsufficientMemoryAreRejectedBeforeStarting()
    {
        var config = new BenchmarkConfig { Targets = [TargetKind.SqlServer] };
        foreach (var engine in Engines.All.Where(engine => engine.Id != "csharp"))
            Assert.Throws<ArgumentException>(() => (config with { EngineIds = ["csharp", engine.Id] }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { MemoryMb = 1024 }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { VirtualMachine = new() { MemoryMb = 2048 } }).Validate());
        (config with { VirtualMachine = new() { MemoryMb = 4096 } }).Validate();
    }

    [Fact]
    public void SqlServerSchemaPreservesNativeTypesUnicodeAndIndexableKeys()
    {
        var workload = WorkloadTemplates.Blank() with { Datasets = [new() { Name = "typed", Columns =
        [new() { Name = "id", Type = SqlValueType.Guid, PrimaryKey = true, Generator = ValueGenerator.Guid },
         new() { Name = "title", Type = SqlValueType.Text, Generator = ValueGenerator.Text },
         new() { Name = "code", Type = SqlValueType.Text, Generator = ValueGenerator.Text, Unique = true },
         new() { Name = "active", Type = SqlValueType.Boolean, Generator = ValueGenerator.Boolean },
         new() { Name = "created", Type = SqlValueType.DateTime, Generator = ValueGenerator.Timestamp },
         new() { Name = "amount", Type = SqlValueType.Decimal, Generator = ValueGenerator.Decimal },
         new() { Name = "score", Type = SqlValueType.Double, Generator = ValueGenerator.Decimal }] }] };
        var schema = SqlWorkloadText.AutoSchema(workload, TargetKind.SqlServer);
        Assert.Contains("[id] UNIQUEIDENTIFIER PRIMARY KEY NOT NULL", schema);
        Assert.Contains("[title] NVARCHAR(MAX)", schema);
        Assert.Contains("[code] NVARCHAR(450)", schema);
        Assert.Contains("[active] BIT", schema);
        Assert.Contains("[created] DATETIME2(6)", schema);
        Assert.Contains("[amount] DECIMAL(28,12)", schema);
        Assert.Contains("[score] FLOAT", schema);
    }

    [Fact]
    public void StoreTemplateUsesExplicitTopDialectAndValidatesAllSqlTargets()
    {
        var workload = WorkloadTemplates.Store();
        var step = workload.Operations.Single(operation => operation.Id == "history").Steps.Single();
        Assert.Contains("TOP (50)", step.Command.For(TargetKind.SqlServer));
        Assert.DoesNotContain("LIMIT", step.Command.For(TargetKind.SqlServer));
        Assert.Contains("LIMIT 50", step.Command.For(TargetKind.PostgreSql));
        (new BenchmarkConfig { Targets = [TargetKind.SqlServer], CustomWorkload = workload }).Validate();
        Assert.Equal(new[] { "key" }, SqlWorkloadText.Parameters("SELECT @@VERSION, [@name], '@text' /* @skip */ FROM items WHERE id=@key"));
    }
}
