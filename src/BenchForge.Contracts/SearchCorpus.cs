using System.Text;

namespace BenchForge.Contracts;

public static class SearchCorpus
{
    public const string Version = "payload-terms16-v1";
    public const int TermLength = 16;
    public static string Text(DataRow row)
    {
        var text = new StringBuilder(row.Payload.Length + row.Payload.Length / TermLength + 32);
        text.Append("benchforge group").Append(row.GroupId);
        for (var index = 0; index < row.Payload.Length; index += TermLength)
            text.Append(' ').Append(row.Payload, index, Math.Min(TermLength, row.Payload.Length - index));
        return text.ToString();
    }
    public static string Term(DataRow row) => row.Payload[..Math.Min(TermLength, row.Payload.Length)];
    public static bool ContainsTerm(string text, string term)
    {
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf(' ', position); if (end < 0) end = text.Length;
            if (text.AsSpan(position, end - position).Equals(term.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
            position = end + 1;
        }
        return false;
    }
}
