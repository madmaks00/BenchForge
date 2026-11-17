namespace BenchForge.Contracts;

public static class SqlScripts
{
    // Oracle executes individual DDL statements; a complete PL/SQL block is sent intact.
    public static IReadOnlyList<string> Statements(string sql, TargetKind target)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];
        if (target != TargetKind.Oracle) return [sql];
        var trimmed = sql.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\A(?:(?:--[^\n]*\n|/\*[\s\S]*?\*/)\s*)*(?:BEGIN|DECLARE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return [trimmed.TrimEnd('/').TrimEnd()];
        var result = new List<string>(); var start = 0;
        for (var index = 0; index < sql.Length; index++)
        {
            var ch = sql[index];
            if ((ch is 'q' or 'Q') && index + 2 < sql.Length && sql[index + 1] == '\'')
            {
                var open = sql[index + 2]; var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
                index += 3;
                while (index + 1 < sql.Length && !(sql[index] == close && sql[index + 1] == '\'')) index++;
                index++;
            }
            else if (ch is '\'' or '"')
            {
                for (++index; index < sql.Length; index++) if (sql[index] == ch)
                { if (index + 1 < sql.Length && sql[index + 1] == ch) index++; else break; }
            }
            else if (ch == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            { while (index < sql.Length && sql[index] != '\n') index++; }
            else if (ch == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            { var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal); index = end < 0 ? sql.Length : end + 1; }
            else if (ch == ';')
            { var statement = sql[start..index].Trim(); if (statement.Length > 0) result.Add(statement); start = index + 1; }
        }
        if (sql[start..].Trim() is { Length: > 0 } tail) result.Add(tail);
        return result;
    }
}
