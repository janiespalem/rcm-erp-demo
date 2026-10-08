using System.Net.Http.Headers;
using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LegacyShiftReports(IHttpClientFactory clients)
{
    private async Task<JsonElement> Fetch(HttpContext http, string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie można odczytać raportów zmianowych.");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
            byte[] buffer = new byte[65536]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            { if (output.Length + read > 8 * 1024 * 1024) throw new CrmFault(503, "Odpowiedź raportów przekracza limit."); output.Write(buffer, 0, read); }
            using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z raportami zmianowymi."); }
        catch (JsonException) { throw new CrmFault(503, "Nieprawidłowa odpowiedź raportów zmianowych."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas odczytu raportów zmianowych."); }
    }
    internal static ShiftReportDto Output(JsonElement row)
    {
        static string Text(JsonElement row, string key) => row.GetProperty(key).GetString()!;
        static DateTimeOffset? Time(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;
        var schema = row.TryGetProperty("schema_version", out var version) ? version.GetInt32() : 1;
        var fields = ShiftReportValidation.ReadFields(row.GetProperty("fields"), schema);
        return new(row.GetProperty("id").GetInt64(), DateOnly.Parse(Text(row, "report_date"), System.Globalization.CultureInfo.InvariantCulture), Text(row, "shift"),
            row.GetProperty("author_id").GetInt64(), Text(row, "author_name"), Text(row, "status"), row.GetProperty("version_id").GetInt64(), schema, fields,
            row.GetProperty("created_at").GetDateTimeOffset(), row.GetProperty("updated_at").GetDateTimeOffset(), Time(row, "finalized_at"),
            row.TryGetProperty("finalized_by_id", out var id) && id.ValueKind != JsonValueKind.Null ? id.GetInt64() : null,
            row.TryGetProperty("finalized_by_name", out var name) && name.ValueKind != JsonValueKind.Null ? name.GetString() : null,
            row.GetProperty("correction_count").GetInt32(), Time(row, "deleted_at"), ShiftReportValidation.Warning(fields), ShiftReportValidation.CompletionErrors(fields));
    }
    public ShiftReportFeatures Features(LegacyUser actor)
    { NativeShiftReports.RequireRead(actor); return new(true, false, false, ShiftReportValidation.Schemas); }
    private async Task<ShiftReportDto[]> All(HttpContext http, LegacyUser actor, DateOnly? from, DateOnly? to, string? shift, bool deleted, CancellationToken ct)
    {
        NativeShiftReports.RequireRead(actor); NativeShiftReports.QueryValidation(from, to, shift);
        if (deleted && !NativeShiftReports.Admin(actor)) throw new CrmFault(403, "Usunięte raporty są dostępne tylko dla administratora.");
        var route = "api/shift-reports?deleted=" + deleted.ToString().ToLowerInvariant();
        if (from is not null) route += "&date_from=" + from.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (to is not null) route += "&date_to=" + to.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (shift is not null) route += "&shift=" + shift;
        List<ShiftReportDto> result = [];
        for (var offset = 0; offset <= 10000; offset += 100)
        {
            var rows = (await Fetch(http, route + "&offset=" + offset, ct)).EnumerateArray().Select(Output).ToArray();
            if (rows.Length > 100 || result.Count + rows.Length > 10000) throw new CrmFault(503, "Zawęź zakres dat raportów.");
            result.AddRange(rows); if (rows.Length < 100) return result.ToArray();
        }
        throw new CrmFault(503, "Zawęź zakres dat raportów.");
    }
    public async Task<Page<ShiftReportDto>> List(HttpContext http, LegacyUser actor, DateOnly? from, DateOnly? to, string? shift, bool deleted, int page, int pageSize, CancellationToken ct)
    {
        NativeShiftReports.QueryValidation(from, to, shift, page, pageSize);
        var rows = await All(http, actor, from, to, shift, deleted, ct);
        return new(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize);
    }
    public async Task<ShiftReportDto> Detail(HttpContext http, LegacyUser actor, long id, CancellationToken ct)
    { NativeShiftReports.RequireRead(actor); return Output(await Fetch(http, $"api/shift-reports/{id}", ct)); }
    public async Task<ShiftReportToday> Today(HttpContext http, LegacyUser actor, CancellationToken ct)
    {
        NativeShiftReports.RequireRead(actor); var response = await Fetch(http, "api/shift-reports/today", ct);
        var reports = response.GetProperty("reports").EnumerateArray().Select(Output).ToArray();
        return new(DateOnly.Parse(response.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture), false, reports, Totals(reports));
    }
    public async Task<ShiftReportTotals> Summary(HttpContext http, LegacyUser actor, DateOnly? from, DateOnly? to, string? shift, CancellationToken ct)
        => Totals(await All(http, actor, from, to, shift, false, ct));
    private static ShiftReportTotals Totals(IEnumerable<ShiftReportDto> reports)
    {
        var rows = reports.Where(row => row.DeletedAt is null && row.Status is "finalized" or "corrected").ToArray();
        return new(rows.LongLength, rows.Sum(row => (long)(row.Fields.Assembled ?? 0)), rows.Sum(row => (long)(row.Fields.Prepared ?? 0)),
            rows.Sum(row => (long)(row.Fields.Poured ?? 0)), rows.Sum(row => (long)(row.Fields.Checked ?? 0)),
            rows.Sum(row => (long)(row.Fields.Demoulded ?? 0)), rows.Sum(row => (long)(row.Fields.Damaged ?? 0)));
    }
    public async Task<ShiftReportAuditDto[]> Audit(HttpContext http, LegacyUser actor, long id, bool drafts, CancellationToken ct)
    {
        NativeShiftReports.RequireRead(actor);
        return (await Fetch(http, $"api/shift-reports/{id}/audit?include_drafts={drafts.ToString().ToLowerInvariant()}", ct)).EnumerateArray().Select(row => new ShiftReportAuditDto(
            row.GetProperty("id").GetInt64(), row.GetProperty("actor_id").GetInt64(), row.GetProperty("actor_name").GetString()!,
            row.GetProperty("action").GetString()!, row.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : row.GetProperty("reason").GetString(),
            row.GetProperty("before").ValueKind == JsonValueKind.Null ? null : row.GetProperty("before").Clone(), row.GetProperty("after").Clone(),
            row.GetProperty("created_at").GetDateTimeOffset())).ToArray();
    }
}
