using System.Reflection;

namespace BenchForge.Worker.CSharp;

public static class DriverIdentity
{
    public static string Of(Type type)
    {
        var assembly = type.Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        return assembly.GetName().Name + " " + version;
    }
}
