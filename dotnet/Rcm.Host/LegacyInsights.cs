using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LegacyInsights(IHttpClientFactory clients)
{
    private async Task<byte[]> Fetch(HttpContext http, string route, long limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route); request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie można odczytać raportu.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            { if (output.Length + read > limit) throw new CrmFault(503, "Raport przekracza dozwolony rozmiar."); output.Write(buffer, 0, read); }
            return output.ToArray();
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z raportami."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas odczytu raportu."); }
    }
    private async Task<JsonElement> Read(HttpContext http, string route, CancellationToken ct)
    {
        try { using var json = JsonDocument.Parse(await Fetch(http, route, 16L * 1024 * 1024, ct)); return json.RootElement.Clone(); }
        catch (JsonException) { throw new CrmFault(503, "Nieprawidłowa odpowiedź raportu."); }
    }
    private static string? T(JsonElement r, string key) => InsightCalculations.Text(r, key);
    private static double? N(JsonElement r, string key) => InsightCalculations.Number(r, key);
    private static long Id(JsonElement r) => r.GetProperty("id").GetInt64();
    private static DateOnly? Date(JsonElement r, string key)
    {
        var text = T(r, key);
        return DateOnly.TryParseExact(text, ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }
    private static Page<T> Page<T>(T[] rows, int page, int size) => new(rows.Skip((page - 1) * size).Take(size).ToArray(), rows.Length, page, size);
    private static ScheduleInsight ScheduleRow(JsonElement r) => new(Id(r), T(r, "order_number"), T(r, "client") ?? "", T(r, "status") ?? "", Date(r, "deadline"), T(r, "branch"));
    public async Task<Page<ProductionInsight>> Production(HttpContext h, string? q, int page, int size, CancellationToken ct)
    {
        NativeInsights.Validate(q, page, size); var json = await Read(h, "api/production", ct);
        var rows = json.EnumerateArray().Select(r => new ProductionInsight(Id(r), T(r, "order_number"), T(r, "client") ?? "", T(r, "status") ?? "", Date(r, "deadline"), T(r, "description"), T(r, "material"),
            r.TryGetProperty("routing", out var routing) && routing.ValueKind == JsonValueKind.Array ? routing.EnumerateArray().Select(s => s.GetString() ?? "").ToArray() : [], N(r, "total_net")))
            .Where(r => InsightCalculations.Matches(q, r.OrderNumber, r.Client, r.Description)).ToArray();
        return Page(rows, page, size);
    }
    public async Task<Page<ScheduleInsight>> Schedule(HttpContext h, string? q, int page, int size, CancellationToken ct)
    {
        NativeInsights.Validate(q, page, size); var json = await Read(h, "api/harmonogram", ct);
        return Page(json.EnumerateArray().Select(ScheduleRow).Where(r => InsightCalculations.Matches(q, r.OrderNumber, r.Client)).ToArray(), page, size);
    }
    public async Task<Page<ProfitabilityInsight>> Profitability(HttpContext h, string? q, int page, int size, CancellationToken ct)
    {
        NativeInsights.Validate(q, page, size); var json = await Read(h, "api/rentownosc", ct);
        var rows = json.EnumerateArray().Select(r => new ProfitabilityInsight(Id(r), T(r, "order_number") ?? "", T(r, "client") ?? "", T(r, "status") ?? "",
            N(r, "cena_pln") ?? 0, N(r, "material_cost_pln"), N(r, "labor_cost_pln"), N(r, "koszt_pln") ?? 0, N(r, "marza_pln") ?? 0, N(r, "marza_pct"), N(r, "actual_hours")))
            .Where(r => InsightCalculations.Matches(q, r.OrderNumber, r.Client)).ToArray(); return Page(rows, page, size);
    }
    public async Task<AnalyticsInsight> Analytics(HttpContext h, CancellationToken ct)
    {
        var r = await Read(h, "api/analytics", ct); var overdue = r.GetProperty("overdue_orders").EnumerateArray().Select(ScheduleRow).ToArray();
        return new((int)(N(r, "total_orders") ?? 0), (int)(N(r, "odrzut_count") ?? 0), N(r, "odrzut_pct") ?? 0, (int)(N(r, "standard_count") ?? 0), (int)(N(r, "niestandard_count") ?? 0),
            N(r, "avg_margin_pct"), (int)(N(r, "orders_in_production") ?? 0), (int)(N(r, "orders_done") ?? 0), N(r, "avg_cycle_days"), N(r, "avg_quote_to_start_days"), N(r, "estimate_accuracy_pct"),
            r.GetProperty("revenue_by_month").EnumerateArray().Select(x => new RevenueMonthInsight(T(x, "month") ?? "", (int)(N(x, "orders") ?? 0), N(x, "revenue_pln") ?? 0)).ToArray(),
            r.GetProperty("top_clients").EnumerateArray().Select(x => new TopClientInsight(T(x, "client") ?? "", (int)(N(x, "orders") ?? 0), N(x, "revenue_pln") ?? 0)).ToArray(), overdue.Take(100).ToArray(), overdue.Length);
    }
    public async Task<BenchmarkInsight> Benchmark(HttpContext h, string? material, string? type, int page, int size, CancellationToken ct)
    {
        NativeInsights.Validate(material, page, size); if (type?.Length > 200) throw CrmFault.Invalid("orderType", "Typ zlecenia może mieć do 200 znaków.");
        var r = await Read(h, "api/benchmarks/price-per-kg?material=" + Uri.EscapeDataString(material ?? "") + (string.IsNullOrEmpty(type) ? "" : "&order_type=" + Uri.EscapeDataString(type)), ct);
        var rows = r.GetProperty("samples").EnumerateArray().Select(x => new BenchmarkSampleInsight((long)(N(x, "order_id") ?? 0), Date(x, "date") ?? DateOnly.FromDateTime(DateTime.UtcNow),
            N(x, "weight_kg") ?? 0, N(x, "total_net") ?? 0, N(x, "pln_kg") ?? 0)).ToArray();
        return new(N(r, "avg_pln_kg") ?? 0, N(r, "min_pln_kg") ?? 0, N(r, "max_pln_kg") ?? 0, (int)(N(r, "count") ?? 0), T(r, "warning"), Page(rows, page, size));
    }
    public async Task<Page<ServiceHistoryInsight>> ServiceHistory(HttpContext h, string? q, int page, int size, CancellationToken ct)
    {
        NativeInsights.Validate(q, page, size); var json = await Read(h, "api/service-history?limit=500", ct);
        var rows = json.EnumerateArray().Select(r => new ServiceHistoryInsight(Id(r), Date(r, "order_date"), T(r, "client"), T(r, "order_type"), T(r, "description"), T(r, "material"),
            N(r, "material_cost"), N(r, "constructor_hours"), N(r, "production_hours"), N(r, "total_price") ?? 0, T(r, "source"), T(r, "source_order_number")))
            .Where(r => InsightCalculations.Matches(q, r.Client, r.OrderType, r.Description, r.Material, r.SourceOrderNumber)).ToArray(); return Page(rows, page, size);
    }
    public async Task<IResult> Export(HttpContext h, CancellationToken ct)
    {
        var bytes = await Fetch(h, "api/export/xlsx", 64L * 1024 * 1024, ct);
        if (!bytes.AsSpan().StartsWith("PK"u8)) throw new CrmFault(503, "Nieprawidłowy plik eksportu.");
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "zlecenia_rcm.xlsx");
    }
}
