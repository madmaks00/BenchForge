namespace BenchForge.Contracts;

public static class SqlScripts
{
    public static IReadOnlyList<string> PortableStatements(string sql, TargetKind target)
    {
        if (target == TargetKind.Oracle) return Statements(sql, target);
        if (string.IsNullOrWhiteSpace(sql)) return [];
        var result = new List<string>(); var start = 0; var blocks = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch is '\'' or '"' or '`' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                for (i++; i < sql.Length; i++)
                {
                    if (sql[i] == '\\' && target is TargetKind.MySql or TargetKind.MariaDb) { i++; continue; }
                    if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) i++; else break; }
                }
            }
            else if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; }
            else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1; i += 2;
                for (; i + 1 < sql.Length && depth > 0; i++)
                    if (sql[i] == '/' && sql[i + 1] == '*') { depth++; i++; } else if (sql[i] == '*' && sql[i + 1] == '/') { depth--; i++; }
                i--;
            }
            else if (ch == '$')
            {
                var end = i + 1; while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                if (end < sql.Length && sql[end] == '$') { var tag = sql[i..(end + 1)]; var close = sql.IndexOf(tag, end + 1, StringComparison.Ordinal); if (close >= 0) i = close + tag.Length - 1; }
            }
            else if (char.IsAsciiLetter(ch))
            {
                var first = i; while (i + 1 < sql.Length && (char.IsAsciiLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_')) i++;
                var word = sql[first..(i + 1)];
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || blocks > 0 && word.Equals("CASE", StringComparison.OrdinalIgnoreCase)) blocks++;
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && blocks > 0) blocks--;
            }
            else if (ch == ';' && blocks == 0) { if (sql[start..i].Trim() is { Length: > 0 } statement) result.Add(statement); start = i + 1; }
        }
        if (sql[start..].Trim() is { Length: > 0 } tail) result.Add(tail);
        return result;
    }

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
