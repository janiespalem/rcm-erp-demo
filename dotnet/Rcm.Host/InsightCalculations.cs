using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Rcm.Contracts;

namespace Rcm.Host;

internal static class InsightCalculations
{
    internal static double Round(double value, int digits)
    {
        if (!double.IsFinite(value)) return value;
        var bits = BitConverter.DoubleToUInt64Bits(value); var exponent = (int)((bits >> 52) & 2047);
        var mantissa = bits & ((1UL << 52) - 1);
        if (exponent != 0) mantissa |= 1UL << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1023 - 52;
        var scale = BigInteger.Pow(10, digits); var numerator = new BigInteger(mantissa) * scale; var denominator = BigInteger.One;
        if (shift >= 0) numerator <<= shift; else denominator <<= -shift;
        var result = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && !result.IsEven) result++;
        var rounded = (double)result / (double)scale; return (bits >> 63) != 0 ? -rounded : rounded;
    }
    internal static ProfitabilityInsight Profit(long id, string number, string client, string status, double price, double weight,
        double weightRate, double fallbackMaterial, double planned, double actual, double laborRate)
    {
        var material = weight * weightRate;
        if (material == 0) material = fallbackMaterial;
        var labor = (actual > 0 ? actual : planned) * laborRate; var cost = material + labor; var margin = price - cost;
        return new(id, number, client, status, Round(price, 2), Round(material, 2), Round(labor, 2), Round(cost, 2), Round(margin, 2),
            price > 0 ? Round(margin / price * 100, 1) : null, actual != 0 ? actual : null);
    }
    internal static string[] Routing(JsonElement processes) => processes.ValueKind == JsonValueKind.Array ? processes.EnumerateArray()
        .Where(p => p.ValueKind == JsonValueKind.Object).Select(p => Text(p, "wydział") ?? Text(p, "name") ?? Text(p, "op") ?? "").Where(p => p.Length > 0).ToArray() : [];
    internal static JsonElement Parameters(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Object) return source;
        if (source.ValueKind == JsonValueKind.String)
            try { using var json = JsonDocument.Parse(source.GetString()!); if (json.RootElement.ValueKind == JsonValueKind.Object) return json.RootElement.Clone(); }
            catch (JsonException) { }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>());
    }
    internal static string? Text(JsonElement row, string key)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(); return string.IsNullOrEmpty(text) ? null : text;
    }
    internal static double? Number(JsonElement row, string key) => double.TryParse(Text(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    internal static ServiceHistoryInsight History(JsonElement row)
    {
        var p = Parameters(row.GetProperty("parameters_json"));
        return new(row.GetProperty("id").GetInt64(), DateOnly.TryParse(Text(row, "order_date"), CultureInfo.InvariantCulture, out var date) ? date : null,
            Text(row, "client"), Text(row, "order_type"), Text(p, "description") ?? Text(row, "order_type"), Text(p, "material"), Number(p, "material_cost"),
            Number(p, "constructor_hours"), Number(p, "production_hours"), Number(row, "total_price_historical") ?? 0, Text(row, "source"), Text(p, "source_order_number"));
    }
    internal static bool Matches(string? q, params string?[] values) => string.IsNullOrWhiteSpace(q) || string.Join(' ', values).ToLowerInvariant().Contains(q.Trim().ToLowerInvariant(), StringComparison.Ordinal);
}
