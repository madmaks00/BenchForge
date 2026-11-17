using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BenchForge.Contracts;

public readonly record struct ValueContext(long Row, long Sequence, int Client, long Key, long BaseRows, IReadOnlyDictionary<string, long> TableRows, IReadOnlyDictionary<string, IReadOnlyList<object>>? Samples = null);

public static class WorkloadData
{
    public static string SampleKey(ValueDefinition value) => value.Reference + "/" + value.SampleColumn + "/" + value.SampleSize.ToString(CultureInfo.InvariantCulture);
    public static ulong StableSeed(ulong seed, string name, long row)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(name)) hash = unchecked((hash ^ b) * 1099511628211UL);
        return seed ^ hash ^ unchecked((ulong)row * 0xd6e8feb86659fd93UL);
    }

    public static object Generate(ValueDefinition value, ValueContext context, ref PortableRandom random)
    {
        if (value.NullPercent > 0 && random.Next() % 1_000_000 < value.NullPercent * 10000) return DBNull.Value;
        object raw = value.Generator switch
        {
            ValueGenerator.Sequence => checked((long)value.Min + context.Sequence - 1),
            ValueGenerator.Integer => Integer(ref random, (long)value.Min, (long)value.Max),
            ValueGenerator.Decimal => decimal.Round(value.Min + (value.Max - value.Min) * ((decimal)(random.Next() >> 11) / 9007199254740992m), value.Scale),
            ValueGenerator.Text => Text(value, context, ref random),
            ValueGenerator.Choice => value.Choices[(int)(random.Next() % (ulong)value.Choices.Count)],
            ValueGenerator.Reference => context.TableRows.TryGetValue(value.Reference!, out var count) && count > 0 ? Integer(ref random, 1, count) : throw new InvalidDataException("Пустая таблица ссылки: " + value.Reference),
            ValueGenerator.Guid => NewGuid(ref random),
            ValueGenerator.Timestamp => DateTimeOffset.FromUnixTimeSeconds(Integer(ref random, (long)value.Min, (long)value.Max)).UtcDateTime,
            ValueGenerator.Boolean => random.Next() % 2 == 0,
            ValueGenerator.Constant => value.Constant is null ? DBNull.Value : value.Constant,
            ValueGenerator.Key => context.Key,
            ValueGenerator.Client => context.Client,
            ValueGenerator.Sample => context.Samples is { } samples && samples.TryGetValue(SampleKey(value), out var sample) && sample.Count > 0 ? sample[(int)(random.Next() % (ulong)sample.Count)] : throw new InvalidDataException("Нет ненулевых значений Sample: " + value.Reference + "." + value.SampleColumn),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        return ConvertValue(raw, value.Type);
    }

    public static object ConvertValue(object? raw, SqlValueType type)
    {
        if (raw is null or DBNull) return DBNull.Value;
        if (raw is JsonElement json) raw = json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText();
        if (raw is null) return DBNull.Value;
        var text = Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        return type switch
        {
            SqlValueType.Int64 => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
            SqlValueType.Int32 => Convert.ToInt32(raw, CultureInfo.InvariantCulture),
            SqlValueType.Decimal => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
            SqlValueType.Double => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
            SqlValueType.Boolean => text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new FormatException("Ожидается true/false или 1/0."),
            SqlValueType.DateTime => raw is DateTime date ? date.ToUniversalTime() : DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            SqlValueType.Guid => raw is Guid guid ? guid : Guid.Parse(text),
            _ => text
        };
    }

    private static long Integer(ref PortableRandom random, long min, long max)
    {
        var width = unchecked((ulong)max - (ulong)min + 1);
        return unchecked((long)((ulong)min + (width == 0 ? random.Next() : random.Next() % width)));
    }
    private static Guid NewGuid(ref PortableRandom random)
    { Span<byte> bytes = stackalloc byte[16]; BitConverter.TryWriteBytes(bytes, random.Next()); BitConverter.TryWriteBytes(bytes[8..], random.Next()); return new Guid(bytes); }
    private static string Text(ValueDefinition value, ValueContext context, ref PortableRandom random)
    {
        var prefix = value.Prefix.Replace("{row}", context.Row.ToString(CultureInfo.InvariantCulture)).Replace("{sequence}", context.Sequence.ToString(CultureInfo.InvariantCulture))
            .Replace("{key}", context.Key.ToString(CultureInfo.InvariantCulture)).Replace("{client}", context.Client.ToString(CultureInfo.InvariantCulture));
        var buffer = new char[value.Length];
        for (var index = 0; index < buffer.Length; index++) buffer[index] = DataGenerator.Alphabet[(int)(random.Next() % 62)];
        return prefix + new string(buffer);
    }

    public static IEnumerable<Dictionary<string, object?>> ReadFile(string path, ImportFormat format, CancellationToken token, char delimiter = ',')
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        if (format == ImportFormat.JsonLines)
        {
            string? line; long number = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested(); number++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 16 * 1024 * 1024) throw new InvalidDataException("Строка JSONL больше 16 МБ.");
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("В строке JSONL " + number + " нужен объект.");
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in json.RootElement.EnumerateObject())
                    if (!row.TryAdd(property.Name, property.Value.Clone())) throw new InvalidDataException("Повторяющееся поле JSON: " + property.Name);
                yield return row;
            }
            yield break;
        }
        using var rows = CsvRecords(reader, token, delimiter).GetEnumerator();
        if (!rows.MoveNext()) throw new InvalidDataException("CSV-файлу нужна строка заголовков.");
        var header = rows.Current;
        if (header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Length) throw new InvalidDataException("Пустые или повторяющиеся заголовки CSV.");
        while (rows.MoveNext())
        {
            if (rows.Current.Length != header.Length) throw new InvalidDataException("Число полей строки CSV отличается от заголовка.");
            yield return header.Select((name, index) => (name, value: (object?)rows.Current[index])).ToDictionary(pair => pair.name, pair => pair.value, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IEnumerable<string[]> CsvRecords(TextReader reader, CancellationToken token, char delimiter = ',')
    {
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false; var closed = false; var started = false;
        int value; long characters = 0;
        while ((value = reader.Read()) >= 0)
        {
            if (++characters % 4096 == 0) token.ThrowIfCancellationRequested();
            var ch = (char)value; started = true;
            if (quoted)
            {
                if (ch == '"') { if (reader.Peek() == '"') { reader.Read(); field.Append('"'); } else { quoted = false; closed = true; } }
                else field.Append(ch);
            }
            else if (ch == delimiter || ch is '\r' or '\n')
            {
                fields.Add(field.ToString()); field.Clear(); closed = false;
                if (ch != delimiter)
                { if (ch == '\r' && reader.Peek() == '\n') reader.Read(); yield return fields.ToArray(); fields.Clear(); started = false; }
            }
            else if (ch == '"' && field.Length == 0 && !closed) quoted = true;
            else { if (closed || ch == '"') throw new InvalidDataException("Некорректные кавычки в CSV."); field.Append(ch); }
            if (field.Length > 16 * 1024 * 1024 || fields.Count > 128) throw new InvalidDataException("Слишком большая запись CSV.");
        }
        token.ThrowIfCancellationRequested();
        if (quoted) throw new InvalidDataException("Незакрытая кавычка в CSV.");
        if (started || fields.Count > 0 || field.Length > 0) { fields.Add(field.ToString()); yield return fields.ToArray(); }
    }
}
