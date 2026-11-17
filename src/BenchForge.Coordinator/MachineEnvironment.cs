using System.Globalization;

namespace BenchForge.Coordinator;

public static class MachineEnvironment
{
    public static Dictionary<string, string> Capture()
    {
        var values = new Dictionary<string, string> { ["machine_name"] = Environment.MachineName,
            ["memory_budget_bytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes.ToString(CultureInfo.InvariantCulture) };
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal) || line.StartsWith("Hardware", StringComparison.Ordinal));
                if (cpu is not null) values["cpu_model"] = cpu[(cpu.IndexOf(':') + 1)..].Trim();
                var memory = File.ReadLines("/proc/meminfo").First(line => line.StartsWith("MemTotal:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                values["host_visible_memory_bytes"] = (long.Parse(memory[1], CultureInfo.InvariantCulture) * 1024).ToString(CultureInfo.InvariantCulture);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (Environment.GetEnvironmentVariable("BENCHFORGE_REMOTE_JOB") is { Length: > 0 } job)
        {
            RemoteAgentClient.ValidateJob(job);
            values["remote_job_id"] = job;
            values["agent_image_id"] = Environment.GetEnvironmentVariable("BENCHFORGE_AGENT_IMAGE_ID") ?? "unknown";
            values["generator_location"] = "agent container on the server; native worker processes; host networking";
        }
        return values;
    }
}
