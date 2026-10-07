using System.Text.Json;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeInsights
{
    public async Task<BenchmarkInsight> Benchmark(string? material, string? orderType, int page, int pageSize, CancellationToken ct)
    {
        Validate(material, page, pageSize);
        if (orderType?.Length > 200) throw CrmFault.Invalid("orderType", "Typ zlecenia może mieć do 200 znaków.");
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var command = new NpgsqlCommand("SELECT row_to_json(r) FROM public.price_history r ORDER BY id", db);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<BenchmarkSampleInsight> rows = []; var count = 0; double total = 0, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        var skip = (page - 1) * pageSize;
        while (await reader.ReadAsync(ct))
        {
            using var record = JsonDocument.Parse(reader.GetString(0)); var row = record.RootElement; var p = InsightCalculations.Parameters(row.GetProperty("parameters_json"));
            if (!Contains(InsightCalculations.Text(p, "material"), material) || !Contains(InsightCalculations.Text(row, "order_type"), orderType)) continue;
            var weight = InsightCalculations.Number(p, "weight_kg") ?? 0; var price = InsightCalculations.Number(p, "pln_kg") ?? 0;
            if (weight == 0 || price == 0) continue;
            if (count >= skip && rows.Count < pageSize) rows.Add(new(0, DateOnly.TryParse(InsightCalculations.Text(row, "order_date"), System.Globalization.CultureInfo.InvariantCulture, out var date) ? date : Today,
                weight, InsightCalculations.Number(row, "total_price_historical") ?? 0, price));
            count++; total += price; minimum = Math.Min(minimum, price); maximum = Math.Max(maximum, price);
        }
        await reader.DisposeAsync(); await tx.CommitAsync(ct);
        return new(count == 0 ? 0 : total / count, count == 0 ? 0 : minimum, count == 0 ? 0 : maximum, count,
            count < 3 ? "Potrzeba minimum 3 próbek dla wiarygodnego benchmarku" : null, new(rows.ToArray(), count, page, pageSize));
    }
    private static bool Contains(string? text, string? value) => string.IsNullOrEmpty(value) || (text ?? "").ToLowerInvariant().Contains(value.ToLowerInvariant(), StringComparison.Ordinal);
    public async Task<Page<ServiceHistoryInsight>> ServiceHistory(string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize); await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string source = "Kopia Lista zleceń usługi.xlsx";
        var rows = await Read<JsonElement>(db, """
            SELECT row_to_json(r) FROM public.price_history r
            WHERE NOT EXISTS(SELECT 1 FROM public.price_history WHERE source=@source) OR r.source=@source
            ORDER BY r.id DESC LIMIT 500
            """, ct, P("source", source));
        var filtered = rows.Select(InsightCalculations.History).Where(r => InsightCalculations.Matches(q, r.Client, r.OrderType, r.Description, r.Material, r.SourceOrderNumber)).ToArray();
        await tx.CommitAsync(ct); return new(filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), filtered.Length, page, pageSize);
    }
}
