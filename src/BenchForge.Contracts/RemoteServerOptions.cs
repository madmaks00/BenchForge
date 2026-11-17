using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BenchForge.Contracts;

public sealed record RemoteServerOptions
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string User { get; init; } = "";
    public int Port { get; init; } = 22;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IdentityFile { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? KnownHostsFile { get; init; }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public void Validate()
    {
        var validHost = Regex.IsMatch(Host ?? "", "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,252}$") || IPAddress.TryParse(Host, out _);
        if (!validHost || Port is < 1 or > 65535 || Name is null || Name.Length > 120 || Name.Any(char.IsControl) ||
            User is null || User.Length > 64 || User.Length > 0 && !Regex.IsMatch(User, "^[A-Za-z0-9_][A-Za-z0-9_.-]*$"))
            throw new ArgumentException("Укажите адрес или SSH-алиас сервера, пользователя и порт 1–65535.");
        foreach (var path in new[] { IdentityFile, KnownHostsFile })
            if (path is not null && (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)))
                throw new ArgumentException("Проверьте путь к SSH-ключу и файлу известных серверов.");
    }

    public RemoteServerOptions ForReport() => this with { IdentityFile = null, KnownHostsFile = null };
}
