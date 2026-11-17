using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class DockerDesktopStartupTests
{
    private static BenchmarkConfig Config => new() { Targets = [TargetKind.PostgreSql], DatasetRows = [100], Clients = [1], Repeats = 1 };

    [Fact]
    public async Task EmbeddedAndRemoteModesNeverContactLocalDocker()
    {
        var host = new FakeHost(); var startup = new DockerDesktopStartup(host);
        foreach (var config in new[] {
            Config with { Targets = [TargetKind.Sqlite, TargetKind.DuckDb] },
            Config with { VirtualMachine = new() },
            Config with { RemoteServer = new() { Host = "example.test", User = "ubuntu" } } })
            await startup.EnsureReadyAsync(config, null, CancellationToken.None);
        Assert.Equal(0, host.Probes); Assert.Equal(0, host.EndpointChecks); Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task RunningLinuxEngineNeedsNoDesktopStart()
    {
        var host = new FakeHost { Answers = new(["linux"]) };
        await new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, CancellationToken.None);
        Assert.Equal(1, host.Probes); Assert.Equal(0, host.Starts); Assert.Equal(0, host.EndpointChecks);
    }

    [Fact]
    public async Task UnavailableEngineStartsOnceAndWaitsForLinuxBeforeReturning()
    {
        var host = new FakeHost { Answers = new([null, null, "linux"]) }; var messages = new List<string>();
        await new DockerDesktopStartup(host, pollInterval: TimeSpan.FromMilliseconds(1)).EnsureReadyAsync(
            Config with { Targets = [TargetKind.Sqlite, TargetKind.PostgreSql] }, messages.Add, CancellationToken.None);
        Assert.Equal(3, host.Probes); Assert.Equal(1, host.EndpointChecks); Assert.Equal(1, host.Starts);
        Assert.Contains("Запускаем Docker Desktop", messages);
        Assert.Contains(messages, message => message.StartsWith("Ожидаем готовности Docker"));
        Assert.Equal("Docker готов · подготовка эксперимента", messages[^1]);
    }

    [Fact]
    public async Task WindowsEngineIsReportedWithoutRestartingOrSwitchingIt()
    {
        var host = new FakeHost { Answers = new(["windows"]) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, CancellationToken.None));
        Assert.Contains("Linux containers", error.Message); Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task WrongEngineAfterStartupIsReportedImmediately()
    {
        var host = new FakeHost { Answers = new([null, "windows"]) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, CancellationToken.None));
        Assert.Equal(1, host.Starts); Assert.Equal(2, host.Probes);
    }

    [Fact]
    public async Task UnavailableRemoteDockerEndpointNeverStartsDesktop()
    {
        var host = new FakeHost { Local = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, CancellationToken.None));
        Assert.Contains("подключение Docker недоступно", error.Message); Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task CancellationBeforeStartupDoesNothing()
    {
        var host = new FakeHost(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, cancellation.Token));
        Assert.Equal(0, host.Probes); Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task CancellationInterruptsReadinessWaitAndKeepsOriginalToken()
    {
        var host = new FakeHost { BlockAfterStart = true }; using var cancellation = new CancellationTokenSource();
        var waiting = new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, cancellation.Token);
        await host.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, host.Starts); Assert.Equal(2, host.Probes);
    }

    [Fact]
    public async Task ReadinessDeadlineProducesAnActionableError()
    {
        var host = new FakeHost { BlockAfterStart = true };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => new DockerDesktopStartup(host, TimeSpan.FromMilliseconds(50)).EnsureReadyAsync(Config, null, CancellationToken.None));
        Assert.Contains("Docker не стал готов", error.Message); Assert.Contains("Откройте Docker Desktop", error.Message); Assert.Equal(1, host.Starts);
    }

    [Fact]
    public async Task DesktopLaunchFailureIsNotRetriedOrHidden()
    {
        var host = new FakeHost { StartFailure = new InvalidOperationException("Docker Desktop не найден.") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerDesktopStartup(host).EnsureReadyAsync(Config, null, CancellationToken.None));
        Assert.Equal("Docker Desktop не найден.", error.Message); Assert.Equal(1, host.Starts); Assert.Equal(1, host.Probes);
    }

    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", true)]
    [InlineData("npipe:////./pipe/docker_engine", true)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("unix:///home/user/.docker/desktop/docker.sock", true)]
    [InlineData("ssh://user@server", false)]
    [InlineData("tcp://server:2376", false)]
    [InlineData("npipe:////server/pipe/docker_engine", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyLocalSocketsPermitAutomaticDesktopStartup(string? endpoint, bool local) =>
        Assert.Equal(local, DockerDesktopHost.IsLocalEndpoint(endpoint));

    private sealed class FakeHost : IDockerDesktopHost
    {
        public Queue<string?> Answers { get; init; } = new();
        public bool Local { get; init; } = true;
        public bool BlockAfterStart { get; init; }
        public Exception? StartFailure { get; init; }
        public int Probes, EndpointChecks, Starts;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> EngineOsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Probes++;
            if (BlockAfterStart && Starts > 0) { Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Answers.TryDequeue(out var os) ? os : null;
        }
        public Task<bool> IsLocalEndpointAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); EndpointChecks++; return Task.FromResult(Local); }
        public Task StartAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Starts++; if (StartFailure is { } error) throw error; return Task.CompletedTask; }
    }
}
