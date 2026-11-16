using System.Runtime.InteropServices;
using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Worker.CSharp;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
var writer = new ProtocolWriter(Console.Out);
await writer.EmitAsync("hello", "", new { engine = "csharp", version = "0.1.0", runtime = RuntimeInformation.FrameworkDescription,
    generator = DataGenerator.Version, histogram = "binary128-us-v1", targets = Catalog.Targets.Select(x => new { x.Kind, x.Scenarios }) });
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
RunCase? run = null;
try
{
    var line = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("Отсутствует команда run.");
    if (line.Length > 1_048_576) throw new InvalidDataException("Слишком большая команда.");
    var command = JsonSerializer.Deserialize<WorkerCommand>(line, Json.Options) ?? throw new InvalidDataException("Пустая команда.");
    if (command.ProtocolVersion != 1 || command.Type != "run" || command.Run is null) throw new InvalidDataException("Ожидается команда run протокола v1.");
    run = command.Run;
    run.Config.Validate();
    if (!run.Config.DatasetRows.Contains(run.Rows) || !run.Config.CaseClients().Contains(run.Clients) || !run.Config.Targets.Contains(run.Target))
        throw new InvalidDataException("Параметры запуска не входят в конфигурацию.");
    _ = Task.Run(async () =>
    {
        try
        {
            while (await Console.In.ReadLineAsync(cancellation.Token) is { } input)
            {
                var next = JsonSerializer.Deserialize<WorkerCommand>(input, Json.Options);
                if (next?.ProtocolVersion == 1 && next.Type == "cancel") { cancellation.Cancel(); return; }
            }
            cancellation.Cancel(); // The owner went away.
        }
        catch (OperationCanceledException) { }
        catch { cancellation.Cancel(); }
    });
    await new BenchmarkEngine(run, writer).RunAsync(cancellation.Token);
    await writer.EmitAsync("completed", run.RunId, new { cancelled = cancellation.IsCancellationRequested });
    return cancellation.IsCancellationRequested ? 2 : 0;
}
catch (OperationCanceledException)
{
    await writer.EmitAsync("completed", run?.RunId ?? "", new { cancelled = true });
    return 2;
}
catch (Exception exception)
{
    var message = exception.Message;
    if (!string.IsNullOrEmpty(run?.Endpoint.Password)) message = message.Replace(run.Endpoint.Password, "[redacted]");
    await writer.EmitAsync("failed", run?.RunId ?? "", new { message });
    return 1;
}
