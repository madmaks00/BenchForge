using System.Text;

namespace BenchForge.Contracts;

public static class SqlWorkloadText
{
    public static string Quote(string identifier, TargetKind target)
    {
        if (!SqlWorkload.Identifier(identifier)) throw new ArgumentException("Некорректное имя SQL: " + identifier);
        return target is TargetKind.MySql or TargetKind.MariaDb ? "`" + identifier + "`" : "\"" + identifier + "\"";
    }
    public static string AutoSchema(SqlWorkload workload, TargetKind target)
    {
        var sql = new StringBuilder();
        foreach (var table in workload.Datasets)
        {
            sql.Append("CREATE TABLE ").Append(Quote(table.Name, target)).Append(" (\n");
            sql.AppendJoin(",\n", table.Columns.Select(column => "  " + Quote(column.Name, target) + " " + TypeName(column, target) +
                (column.PrimaryKey ? " PRIMARY KEY NOT NULL" : column.Nullable ? "" : " NOT NULL") + (column.Unique && !column.PrimaryKey ? " UNIQUE" : ""))
                .Concat(table.Columns.Where(column => column.Generator == ValueGenerator.Reference).Select(column => "  FOREIGN KEY (" + Quote(column.Name, target) + ") REFERENCES " +
                    Quote(column.Reference!, target) + "(" + Quote(workload.Datasets.Single(parent => parent.Name == column.Reference).Columns.Single(key => key.PrimaryKey).Name, target) + ")")));
            sql.Append("\n);\n");
        }
        return sql.ToString();
    }
    private static string TypeName(ValueDefinition value, TargetKind target) => value.Type switch
    {
        SqlValueType.Int64 => "BIGINT", SqlValueType.Int32 => "INTEGER", SqlValueType.Decimal => "DECIMAL(28,12)",
        SqlValueType.Double => target == TargetKind.PostgreSql ? "DOUBLE PRECISION" : "DOUBLE",
        SqlValueType.Boolean => "BOOLEAN", SqlValueType.DateTime => target == TargetKind.PostgreSql ? "TIMESTAMPTZ" : target == TargetKind.Sqlite ? "TEXT" : "DATETIME(6)",
        SqlValueType.Guid => target == TargetKind.PostgreSql ? "UUID" : "CHAR(36)",
        _ => target is TargetKind.MySql or TargetKind.MariaDb && (value.PrimaryKey || value.Unique) ? "VARCHAR(191)" : "TEXT"
    };

    // Read parameter markers outside comments, quoted identifiers, strings and PostgreSQL dollar strings.
    public static string[] Parameters(string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sql.Length; index++)
        {
            var ch = sql[index];
            if (ch is '\'' or '"' or '`' or '[')
            {
                var end = ch == '[' ? ']' : ch;
                for (++index; index < sql.Length; index++)
                    if (sql[index] == end) { if (index + 1 < sql.Length && sql[index + 1] == end) index++; else break; }
            }
            else if (ch == '-' && index + 1 < sql.Length && sql[index + 1] == '-') { while (index < sql.Length && sql[index] != '\n') index++; }
            else if (ch == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            { var depth = 1; index += 2; for (; index < sql.Length && depth > 0; index++) { if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*') { depth++; index++; } else if (sql[index] == '*' && index + 1 < sql.Length && sql[index + 1] == '/') { depth--; index++; } } index--; }
            else if (ch == '$')
            {
                var end = index + 1; while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                if (end < sql.Length && sql[end] == '$') { var marker = sql[index..(end + 1)]; var close = sql.IndexOf(marker, end + 1, StringComparison.Ordinal); if (close >= 0) index = close + marker.Length - 1; }
            }
            else if (ch == '@' && (index == 0 || sql[index - 1] != '@') && index + 1 < sql.Length && (char.IsAsciiLetter(sql[index + 1]) || sql[index + 1] == '_'))
            { var start = ++index; while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_')) index++; names.Add(sql[start..index]); index--; }
        }
        return names.ToArray();
    }
}
