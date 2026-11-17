using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class RemoteServerTests
{
    private static RemoteServerOptions Server => new() { Name = "Test server", Host = "example.test", User = "ubuntu", IdentityFile = "private-key" };
    private static BenchmarkConfig Config => new() { RemoteServer = Server, DatasetRows = [100], Clients = [1], Repeats = 1, WarmupSeconds = 0, DurationSeconds = .1, Scenarios = [Scenario.PointRead] };

    [Theory]
    [InlineData("--help")]
    [InlineData("a; touch /tmp/unwanted")]
    [InlineData("host\ncommand")]
    public void InvalidHostIsRejected(string host) => Assert.Throws<ArgumentException>(() => (Server with { Host = host }).Validate());

    [Fact]
    public void RemoteAndVmModesAreMutuallyExclusive() =>
        Assert.Throws<ArgumentException>(() => (Config with { VirtualMachine = new() }).Validate());

    [Fact]
    public void SshUsesStrictTrustAndQuotesAllRemoteCommands()
    {
        var args = SshTransport.Arguments(Server, "echo 'quoted'; printf '$HOME'");
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("ForwardAgent=no", args);
        Assert.Equal(new[] { "--", "example.test", "sh -c 'echo '\\''quoted'\\''; printf '\\''$HOME'\\'''" }, args[^3..]);
        Assert.Null(Server.ForReport().IdentityFile);
        Assert.Contains(SshTransport.Arguments(Server with { KnownHostsFile = "known hosts" }, "true"), value => value.StartsWith("UserKnownHostsFile=\"") && value.EndsWith("known hosts\""));
    }

    [Fact]
    public async Task RemoteDispatchDoesNotRequireLocalDockerOrLanguageRuntimes()
    {
        using var folder = new Folder();
        var remote = new FakeRunner();
        Assert.Empty(await ExperimentRunner.PreflightAsync(Config, CancellationToken.None, remote: remote));
        var outcome = await new ExperimentRunner(remote: remote).RunAsync(Config, folder.Path, CancellationToken.None);
        Assert.Equal(1, remote.Checks); Assert.Equal(1, remote.Runs); Assert.Equal("completed", outcome.Report.Status);
    }

    [Fact]
    public async Task BundleContainsOnlyBuildSourcesAndHashesContent()
    {
        using var folder = new Folder();
        var root = System.IO.Path.Combine(folder.Path, "source"); Directory.CreateDirectory(root);
        foreach (var file in ServerAgentBundle.Files(ServerAgentBundle.SourceDirectory()))
        {
            var destination = System.IO.Path.Combine(root, file); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(System.IO.Path.Combine(ServerAgentBundle.SourceDirectory(), file), destination);
        }
        File.WriteAllText(System.IO.Path.Combine(root, "id_ed25519"), "private");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "workers", "dist"));
        File.WriteAllText(System.IO.Path.Combine(root, "workers", "dist", "private.json"), "private");
        var a = await ServerAgentBundle.CreateAsync(["go", "csharp"], CancellationToken.None, root, folder.Path);
        var b = await ServerAgentBundle.CreateAsync(["csharp", "go"], CancellationToken.None, root, folder.Path);
        Assert.Equal(a, b); Assert.Equal("csharp,go", a.Engines);
        using (var file = File.OpenRead(a.Archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            { Assert.DoesNotContain("private", entry.Name); Assert.DoesNotContain("/bin/", entry.Name); Assert.DoesNotContain("/obj/", entry.Name); }
        }
        File.AppendAllText(System.IO.Path.Combine(root, "server", "entrypoint.sh"), "\n# content changed\n");
        var changed = await ServerAgentBundle.CreateAsync(["csharp", "go"], CancellationToken.None, root, folder.Path);
        Assert.NotEqual(a.SourceHash, changed.SourceHash);
    }

    [Fact]
    public async Task RemoteResultsKeepServerMeasurementsAndRemoveControllerKeyPaths()
    {
        using var folder = new Folder(); var fake = new FakeTransport(folder.Path);
        var runner = new RemoteExperimentRunner(new(fake), new(folder.Path + "/tickets"));
        var result = await runner.RunAsync(Config, folder.Path + "/reports", null, CancellationToken.None);
        Assert.Equal("completed", result.Report.Status);
        Assert.Equal(42, Assert.Single(result.Report.Results).Server!.MemorySampledMaxMb);
        Assert.Equal("Remote Linux", result.Report.Environment["os"]);
        Assert.Equal("server-container-os", result.Report.Environment["agent_os"]);
        Assert.True(result.Report.Environment.ContainsKey("controller_os"));
        Assert.Null(result.Report.Config.RemoteServer!.IdentityFile);
        Assert.Null(fake.UploadedConfig!.RemoteServer); Assert.Null(fake.UploadedConfig.VirtualMachine);
        Assert.Equal(1, fake.Starts); Assert.Equal(1, fake.Cleanups);
        Assert.DoesNotContain("private-key", await File.ReadAllTextAsync(result.Folder + "/report.json"));
    }

    [Fact]
    public async Task ConnectionLossAndResumeNeverRestartQueriesOrDuplicateResults()
    {
        using var folder = new Folder(); var fake = new FakeTransport(folder.Path) { LoseConnection = true };
        var store = new RemoteJobStore(folder.Path + "/tickets"); var runner = new RemoteExperimentRunner(new(fake), store);
        var pending = await runner.RunAsync(Config, folder.Path + "/reports", null, CancellationToken.None);
        Assert.Equal("disconnected", pending.Report.Status); Assert.Null(pending.Report.FinishedAt);
        Assert.NotNull(store.Latest(Server)); Assert.Single(pending.Report.Results);
        fake.LoseConnection = false;
        var finished = await runner.ResumeAsync(Server, null, CancellationToken.None);
        Assert.Equal(pending.Folder, finished.Folder); Assert.Equal("completed", finished.Report.Status);
        Assert.Single(finished.Report.Results); Assert.Equal(1, fake.Starts); Assert.Null(store.Latest(Server));
    }

    [Fact]
    public async Task CancelDuringAmbiguousLaunchTracksAndCancelsTheSameJob()
    {
        using var folder = new Folder(); using var stop = new CancellationTokenSource();
        var fake = new FakeTransport(folder.Path) { CancelDuringStart = stop };
        var result = await new RemoteExperimentRunner(new(fake), new(folder.Path + "/tickets"))
            .RunAsync(Config, folder.Path + "/reports", null, stop.Token);
        Assert.Equal("cancelled", result.Report.Status); Assert.Equal(1, fake.Starts); Assert.Equal(1, fake.Cancels);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task InvalidArchiveOrDifferentConfigIsNotAccepted(bool checksum, bool config, bool unsafeEntry)
    {
        using var folder = new Folder(); var fake = new FakeTransport(folder.Path) { BadChecksum = checksum, BadConfig = config, UnsafeEntry = unsafeEntry };
        var store = new RemoteJobStore(folder.Path + "/tickets");
        var result = await new RemoteExperimentRunner(new(fake), store).RunAsync(Config, folder.Path + "/reports", null, CancellationToken.None);
        Assert.Equal("disconnected", result.Report.Status); Assert.Equal(0, fake.Cleanups); Assert.NotNull(store.Latest(Server));
        Assert.False(File.Exists(folder.Path + "/escaped"));
    }

    [Fact]
    public async Task FailedAgentWithoutReportShowsErrorAndCleansOwnedResources()
    {
        using var folder = new Folder(); var fake = new FakeTransport(folder.Path) { MissingReport = true };
        var result = await new RemoteExperimentRunner(new(fake), new(folder.Path + "/tickets"))
            .RunAsync(Config, folder.Path + "/reports", null, CancellationToken.None);
        Assert.Equal("failed", result.Report.Status); Assert.Contains("agent failed", result.Report.Failures);
        Assert.Equal(1, fake.Cleanups);
    }

    private sealed class FakeRunner : IRemoteExperimentRunner
    {
        public int Checks, Runs;
        public Task CheckAsync(BenchmarkConfig config, CancellationToken token) { Checks++; return Task.CompletedTask; }
        public Task<(BenchmarkReport Report, string Folder)> RunAsync(BenchmarkConfig config, string reportsRoot, Action<RunUpdate>? update, CancellationToken token)
        { Runs++; return Task.FromResult((new BenchmarkReport { Config = config, Status = "completed" }, reportsRoot)); }
    }

    private sealed class FakeTransport(string folder) : IRemoteTransport
    {
        public int Starts, Cancels, Cleanups;
        public bool LoseConnection, BadChecksum, BadConfig, UnsafeEntry, MissingReport;
        public CancellationTokenSource? CancelDuringStart;
        public BenchmarkConfig? UploadedConfig;
        private string _job = "", _archive = "";
        private PhaseResult Row => new() { Target = TargetKind.Sqlite, DatasetRows = 100, Clients = 1, Repeat = 1,
            Phase = nameof(Scenario.PointRead), Verified = true, ElapsedSeconds = .1, SuccessfulOperations = 10, OperationsPerSecond = 100, Server = new(42, 1, 0, 0, 1) };
        public Task UploadAsync(RemoteServerOptions server, string localFile, string remoteFile, CancellationToken token)
        { UploadedConfig = JsonSerializer.Deserialize<BenchmarkConfig>(File.ReadAllText(localFile), Json.Options); _job = remoteFile.Split('/')[^2]; return Task.CompletedTask; }
        public Task DownloadAsync(RemoteServerOptions server, string remoteFile, string localFile, CancellationToken token)
        { File.Copy(_archive, localFile, true); return Task.CompletedTask; }
        public async Task FollowAsync(RemoteServerOptions server, string command, Func<string, Task> receive, CancellationToken token)
        {
            var offset = long.Parse(Regex.Match(command, "'(\\d+)'$").Groups[1].Value);
            if (offset == 0) await receive(JsonSerializer.Serialize(new RunUpdate("result", Result: Row), Json.Options));
            if (LoseConnection) throw new SshConnectionException("injected disconnect");
        }
        public Task<string> RunAsync(RemoteServerOptions server, string command, CancellationToken token, int timeoutSeconds = 45)
        {
            if (command.Contains("BENCHFORGE_SERVER_V1")) return Task.FromResult("BENCHFORGE_SERVER_V1\n/home/test\nremote\nx86_64\nRemote Linux\nLinux test");
            if (command.Contains("'start'"))
            {
                Starts++;
                if (CancelDuringStart is { } stop) { stop.Cancel(); throw new OperationCanceledException(stop.Token); }
            }
            if (command.Contains("'state'")) return Task.FromResult(CancelDuringStart is not null ? "cancelled" : MissingReport ? "failed" : "completed");
            if (command.Contains("'cancel'")) Cancels++;
            if (command.Contains("'cleanup'")) Cleanups++;
            if (command.Contains("'error'")) return Task.FromResult("agent failed");
            if (command.Contains("'collect'"))
            {
                if (MissingReport) return Task.FromResult("NO_REPORT");
                _archive = System.IO.Path.Combine(folder, "remote.tar.gz");
                var report = new BenchmarkReport { Config = BadConfig ? UploadedConfig! with { Seed = 666 } : UploadedConfig!,
                    Status = CancelDuringStart is not null ? "cancelled" : "completed", FinishedAt = DateTimeOffset.UtcNow, Results = [Row],
                    Environment = new() { ["os"] = "server-container-os", ["remote_job_id"] = _job } };
                using (var file = File.Create(_archive))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                using (var tar = new TarWriter(gzip))
                {
                    using var data = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, Json.Options)));
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, UnsafeEntry ? "../escaped" : "report.json") { DataStream = data });
                }
                var checksum = BadChecksum ? "invalid" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_archive)));
                return Task.FromResult(checksum + "  report.tar.gz");
            }
            return Task.FromResult("");
        }
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "benchforge-remote-test-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
