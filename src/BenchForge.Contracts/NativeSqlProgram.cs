using System.Globalization;

namespace BenchForge.Contracts;

public sealed record NativeSqlCommand(string Sql, string[] Names, SqlValueType[] Types);
public sealed record NativeSqlDataset(SqlDataset Definition, long Rows, string Seed, NativeSqlCommand Insert, string? InputFile);
public sealed record NativeSqlOperation(SqlOperation Definition, NativeSqlCommand[] Commands);
public sealed record NativeSqlCheck(SqlCheck Definition, NativeSqlCommand Command);
public sealed record NativeSqlUnit(string Phase, string? Operation);
public sealed record NativeSqlProgram(string Fingerprint, string[] Drop, string[] Schema, string[] AfterLoad,
    NativeSqlDataset[] Datasets, NativeSqlOperation[] Operations, NativeSqlCheck[] Checks, NativeSqlUnit[] Units);

/// <summary>Only SQL dialect/placeholder compilation is shared. Native engines generate values and execute their own queries.</summary>
public static class PortableSql
{
    public static NativeSqlCommand Command(string sql, IReadOnlyDictionary<string, SqlValueType> types, TargetKind target, string engine)
    {
        if (engine == "python" && Marker(target, engine, 1) == "%s" && SqlWorkloadText.Parameters(sql).Length > 0)
            sql = sql.Replace("%", "%%", StringComparison.Ordinal);
        var spans = SqlWorkloadText.ParameterTokens(sql); var names = new List<string>(); var kinds = new List<SqlValueType>();
        var result = new System.Text.StringBuilder(); var start = 0;
        foreach (var (position, length, name) in spans)
        {
            result.Append(sql.AsSpan(start, position - start)); names.Add(name); var type = types.GetValueOrDefault(name, SqlValueType.Int64); kinds.Add(type);
            result.Append(Cast(Marker(target, engine, names.Count), type, target)); start = position + length;
        }
        result.Append(sql.AsSpan(start)); return new(result.ToString(), names.ToArray(), kinds.ToArray());
    }
    public static string ReadSql(string sql, SqlResultMode mode, TargetKind target, string engine)
    {
        // The direct DuckDB C adapters decode text. Normalize the projection before
        // materialization because legacy scalar access cannot decode every DuckDB type.
        if (target != TargetKind.DuckDb || engine is not ("rust" or "node" or "go") || mode == SqlResultMode.NonQuery)
            return sql;
        var query = sql.Trim().TrimEnd(';');
        if (!System.Text.RegularExpressions.Regex.IsMatch(query, @"\A(?:(?:--[^\n]*\n|/\*[\s\S]*?\*/)\s*)*(?:SELECT|VALUES|TABLE|WITH)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return sql;
        return "SELECT COLUMNS(*)::VARCHAR FROM (\n" + query + "\n) AS benchforge_result";
    }

    public static string Marker(TargetKind target, string engine, int index) => engine == "java" ? "?" :
        engine == "python" && target is TargetKind.PostgreSql or TargetKind.CockroachDb or TargetKind.MySql or TargetKind.MariaDb or TargetKind.SqlServer ? "%s" :
        target == TargetKind.Oracle ? engine == "csharp" || engine == "python" ? ":p" + index : ":" + index :
        engine == "csharp" || target == TargetKind.SqlServer ? "@p" + index :
        target is TargetKind.PostgreSql or TargetKind.CockroachDb ? "$" + index : "?";

    private static string Cast(string marker, SqlValueType type, TargetKind target)
    {
        var pg = target is TargetKind.PostgreSql or TargetKind.CockroachDb;
        if (pg) marker = "CAST(" + marker + " AS TEXT)";
        if (type == SqlValueType.Text) return marker;
        if (target == TargetKind.Oracle && type == SqlValueType.Guid) return "HEXTORAW(" + marker + ")";
        if (target == TargetKind.Oracle && type == SqlValueType.DateTime) return "TO_TIMESTAMP(" + marker + ", 'YYYY-MM-DD HH24:MI:SS.FF6')";
        if (target == TargetKind.Sqlite && type is SqlValueType.DateTime or SqlValueType.Guid) return marker;
        var mysql = target is TargetKind.MySql or TargetKind.MariaDb;
        var kind = type switch
        {
            SqlValueType.Int64 => target == TargetKind.Oracle ? "NUMBER(19)" : mysql ? "SIGNED" : "BIGINT",
            SqlValueType.Int32 => target == TargetKind.Oracle ? "NUMBER(10)" : mysql ? "SIGNED" : "INTEGER",
            SqlValueType.Decimal => target == TargetKind.Sqlite ? "NUMERIC" : "DECIMAL(28,12)",
            SqlValueType.Double => target == TargetKind.Oracle ? "BINARY_DOUBLE" : pg ? "DOUBLE PRECISION" : target == TargetKind.SqlServer ? "FLOAT" : target == TargetKind.Sqlite ? "REAL" : "DOUBLE",
            SqlValueType.Boolean => target == TargetKind.Oracle ? "NUMBER(1)" : mysql ? "UNSIGNED" : target == TargetKind.SqlServer ? "BIT" : target == TargetKind.Sqlite ? "INTEGER" : "BOOLEAN",
            SqlValueType.DateTime => target == TargetKind.SqlServer ? "DATETIME2(6)" : mysql ? "DATETIME(6)" : "TIMESTAMP",
            SqlValueType.Guid => target == TargetKind.SqlServer ? "UNIQUEIDENTIFIER" : pg || target == TargetKind.DuckDb ? "UUID" : "CHAR(36)",
            _ => "TEXT"
        };
        return "CAST(" + marker + " AS " + kind + ")";
    }

    public static string? Text(object? value, SqlValueType type, TargetKind target)
    {
        if (value is null or DBNull) return null;
        if (type == SqlValueType.Guid) { var guid = value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!); return target == TargetKind.Oracle ? Convert.ToHexString(guid.ToByteArray()) : guid.ToString(); }
        if (type == SqlValueType.DateTime) return ((DateTime)WorkloadData.ConvertValue(value, type)).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        if (type == SqlValueType.Boolean) return (bool)WorkloadData.ConvertValue(value, type) ? "1" : "0";
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
