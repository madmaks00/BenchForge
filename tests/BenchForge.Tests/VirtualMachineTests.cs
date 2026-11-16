using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class VirtualMachineTests
{
    [Fact]
    public async Task EachDatabaseAndRepeatGetsItsOwnCaseAndKeepsGuestMetrics()
    {
        using var folder = new TestFolder();
        var host = new FakeMachines();
        var config = Config() with { Targets = [TargetKind.Sqlite, TargetKind.PostgreSql], DatasetRows = [100, 200], Repeats = 2 };
        var outcome = await new ExperimentRunner(virtualMachines: host).RunAsync(config, folder.Path, CancellationToken.None);
        Assert.Equal("completed", outcome.Report.Status);
        Assert.Equal(8, host.Cases.Count);
        Assert.Equal(8, host.Cases.Distinct().Count());
        Assert.Equal(new[] { 1, 1, 1, 1, 2, 2, 2, 2 }, outcome.Report.Results.Select(row => row.Repeat));
        Assert.All(outcome.Report.Results, row => Assert.Equal(42, row.Server!.MemorySampledMaxMb));
        Assert.Equal("guest-linux", outcome.Report.Environment["os"]);
        Assert.True(outcome.Report.Environment.ContainsKey("host_os"));
        Assert.Equal(8, outcome.Report.Engines.Count);
        var saved = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(System.IO.Path.Combine(outcome.Folder, "report.json")), Json.Options)!;
        Assert.Equal(config.VirtualMachine, saved.Config.VirtualMachine);
        Assert.Equal(8, saved.Results.Count);
    }

    [Fact]
    public async Task EachEngineGetsItsOwnCleanVmCase()
    {
        using var folder = new TestFolder();
        var host = new FakeMachines();
        var outcome = await new ExperimentRunner(virtualMachines: host).RunAsync(Config() with { EngineIds = ["csharp", "java", "go"] }, folder.Path, CancellationToken.None);
        Assert.Equal("completed", outcome.Report.Status);
        Assert.Equal(3, host.Cases.Count);
        Assert.Equal(new[] { "csharp", "java", "go" }, outcome.Report.Results.Select(row => row.Engine));
        Assert.Equal(3, outcome.Report.Engines.Count);
    }

    [Fact]
    public async Task VmCancellationPreservesStreamedResults()
    {
        using var folder = new TestFolder();
        using var stop = new CancellationTokenSource();
        var host = new FakeMachines { AfterResult = stop.Cancel };
        var result = await new ExperimentRunner(virtualMachines: host).RunAsync(Config() with { Repeats = 2 }, folder.Path, stop.Token);
        Assert.Equal("cancelled", result.Report.Status);
        Assert.Single(result.Report.Results);
        Assert.Single(host.Cases);
        Assert.True(File.Exists(System.IO.Path.Combine(result.Folder, "report.json")));
    }

    [Fact]
    public async Task FailedVmCleanupStopsTheRemainingCases()
    {
        using var folder = new TestFolder();
        var host = new FakeMachines { FailCleanup = true };
        var result = await new ExperimentRunner(virtualMachines: host).RunAsync(Config() with { Repeats = 3 }, folder.Path, CancellationToken.None);
        Assert.Equal("failed", result.Report.Status);
        Assert.Single(host.Cases);
        Assert.Single(result.Report.Results);
        Assert.Contains("cleanup", Assert.Single(result.Report.Failures));
    }

    [Fact]
    public async Task InterruptedStartDeletesOnlyTheNewClone()
    {
        using var folder = new TestFolder();
        var commands = new StartFailureCommands();
        var runner = new VirtualMachineCaseRunner(commands);
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Config(), TargetKind.Sqlite, 100, 1, id,
            folder.Path, _ => Task.CompletedTask, CancellationToken.None));
        var deletion = Assert.Single(commands.Calls, args => args[0] == "delete");
        Assert.Equal(new[] { "delete", "--purge", "benchforge-case-" + id }, deletion);
        Assert.DoesNotContain(commands.Calls, args => args.Contains("--all"));
        Assert.DoesNotContain(commands.Calls.Where(args => args[0] == "delete"), args => args.Contains("benchforge-base-v1"));
    }

    [Theory]
    [InlineData("../base")]
    [InlineData("--all")]
    [InlineData("base;cmd")]
    public void InvalidTemplateNamesFailBeforeStartingProcesses(string name) =>
        Assert.Throws<ArgumentException>(() => (Config() with { VirtualMachine = new() { Template = name } }).Validate());

    [Fact]
    public async Task CancelDuringTemplateCreationWaitsForItsDiskThenDeletesIt()
    {
        using var stop = new CancellationTokenSource();
        var name = "benchforge-test-" + Guid.NewGuid().ToString("N");
        var commands = new TemplateCancellationCommands(stop, name);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new VirtualMachineTemplate(commands)
            .PrepareAsync(new() { Template = name }, [], _ => { }, stop.Token));
        Assert.True(commands.Deleted);
        Assert.False(commands.LaunchWasCancellable);
    }

    private static BenchmarkConfig Config() => new()
    {
        DatasetRows = [100], Clients = [1], Repeats = 1, WarmupSeconds = 0,
        Plan = LoadPlan.Single(LoadProfile.Steady, 1, .1), VirtualMachine = new()
    };

    private sealed class FakeMachines : IVirtualMachineCaseRunner
    {
        public List<string> Cases { get; } = [];
        public Action? AfterResult { get; init; }
        public bool FailCleanup { get; init; }
        public Task CheckAsync(VirtualMachineOptions options, CancellationToken token) => Task.CompletedTask;
        public async Task<BenchmarkReport> RunAsync(BenchmarkConfig config, TargetKind target, long rows, int clients, string caseId,
            string reportFolder, Func<RunUpdate, Task> receive, CancellationToken token)
        {
            Cases.Add(caseId);
            var stage = config.Plan!.Stages[0];
            var result = new PhaseResult { Engine = config.EngineIds.Single(), Target = target, DatasetRows = rows, Clients = clients, Repeat = 1,
                Phase = nameof(Scenario.PointRead), Stage = new(1, stage.Name, stage.Seconds, stage.OperationsPerSecond),
                Verified = true, ElapsedSeconds = .1, SuccessfulOperations = 10, OperationsPerSecond = 100, Server = new(42, 1, 0, 0, 1) };
            await receive(new("result", Result: result));
            AfterResult?.Invoke();
            token.ThrowIfCancellationRequested();
            if (FailCleanup) throw new VirtualMachineCleanupException("cleanup failed", new IOException());
            return new BenchmarkReport { Config = config with { VirtualMachine = null }, Status = "completed", Results = [result],
                Environment = new() { ["os"] = "guest-linux" }, Engines = new() { [caseId] = new(config.EngineIds.Single(), "runtime", "driver", "db", new()) } };
        }
    }

    private sealed class StartFailureCommands : IVmCommandClient
    {
        public List<string[]> Calls { get; } = [];
        private string? _clone;
        public Task<string> RunAsync(string[] arguments, CancellationToken token, int timeoutSeconds = 120)
        {
            Calls.Add(arguments);
            return arguments[0] switch
            {
                "list" => Task.FromResult(JsonSerializer.Serialize(new { list = _clone is null ? Array.Empty<object>() : new object[] { new { name = _clone, state = "Stopped" } } })),
                "clone" => Create(arguments[^1]),
                "start" => throw new InvalidOperationException("injected startup error"),
                "help" => Task.FromResult("--purge"),
                _ => Task.FromResult("")
            };
        }
        private Task<string> Create(string name) { _clone = name; return Task.FromResult(""); }
        public Task<int> RunGuestAsync(string[] arguments, Func<RunUpdate, Task> receive, Func<Task> requestCancellation, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TemplateCancellationCommands(CancellationTokenSource stop, string name) : IVmCommandClient
    {
        private bool _created;
        public bool Deleted { get; private set; }
        public bool LaunchWasCancellable { get; private set; }
        public Task<string> RunAsync(string[] arguments, CancellationToken token, int timeoutSeconds = 120)
        {
            if (arguments[0] == "launch")
            {
                var cloudInitIndex = Array.IndexOf(arguments, "--cloud-init");
                Assert.True(cloudInitIndex > 0);
                Assert.Equal("#cloud-config\ntimezone: Etc/UTC\n", File.ReadAllText(arguments[cloudInitIndex + 1]));
                LaunchWasCancellable = token.CanBeCanceled;
                _created = true;
                stop.Cancel();
            }
            if (arguments[0] == "delete")
            {
                Assert.Equal(new[] { "delete", "--purge", name }, arguments);
                Deleted = true;
            }
            return Task.FromResult(arguments[0] == "list"
                ? JsonSerializer.Serialize(new { list = _created ? new object[] { new { name, state = "Stopped" } } : [] })
                : "--purge");
        }
        public Task<int> RunGuestAsync(string[] arguments, Func<RunUpdate, Task> receive, Func<Task> requestCancellation, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "benchforge-vm-test-" + Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
