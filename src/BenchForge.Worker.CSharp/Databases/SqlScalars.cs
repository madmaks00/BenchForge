using System.Globalization;
using System.Numerics;

namespace BenchForge.Worker.CSharp.Databases;

internal static class SqlScalars
{
    public static long Int64(object? value) => value is BigInteger big ? checked((long)big) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    public static decimal Decimal(object value) => value is BigInteger big ? checked((decimal)big) : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
