using System.Net.Http.Headers;
using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LegacyTemplates(IHttpClientFactory clients)
{
    private async Task<byte[]> Fetch(HttpContext http, string route, long limit, bool document, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient(document ? "legacy-documents" : "legacy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie można odczytać szablonu.");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); byte[] buffer = new byte[65536]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            { if (output.Length + read > limit) throw new CrmFault(503, "Odpowiedź katalogu przekracza limit."); output.Write(buffer, 0, read); }
            return output.ToArray();
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z katalogiem."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas odczytu katalogu."); }
    }
    private async Task<JsonElement[]> Rows(HttpContext http, string route, CancellationToken ct)
    {
        try { return JsonSerializer.Deserialize<JsonElement[]>(await Fetch(http, route, 8 * 1024 * 1024, false, ct)) ?? []; }
        catch (JsonException) { throw new CrmFault(503, "Nieprawidłowa odpowiedź katalogu."); }
    }
    private static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static JsonElement Array(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value : JsonSerializer.SerializeToElement(System.Array.Empty<object>());
    private static ProductTemplateDto Dto(JsonElement row) => new(row.GetProperty("id").GetInt64(), Text(row, "name") ?? "", Text(row, "category") ?? "",
        Array(row, "operations_json"), Array(row, "materials_json"), Array(row, "instruction_blocks"), Array(row, "machines_json"),
        Number(row, "base_price_pln"), Number(row, "margin_pct") ?? 0, row.GetProperty("is_active").GetBoolean(), Text(row, "project_code"), Text(row, "position_nr"), Text(row, "notes"),
        !string.IsNullOrEmpty(Text(row, "drawing_path")), row.TryGetProperty("version_id", out var version) && version.TryGetInt64(out var v) ? v : 1);
    public async Task<Page<ProductTemplateDto>> List(HttpContext http, string? q, string? category, string? project, int page, int pageSize, CancellationToken ct)
    {
        NativeTemplates.QueryValidation(q, category, project, page, pageSize);
        var rows = (await Rows(http, "api/templates", ct)).Select(Dto).Where(r => (string.IsNullOrEmpty(category) || r.Category == category)
            && (string.IsNullOrEmpty(project) || r.ProjectCode == project)
            && (string.IsNullOrWhiteSpace(q) || (r.Name + " " + r.ProjectCode + " " + r.PositionNumber).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.ProjectCode, StringComparer.Ordinal).ThenBy(r => r.PositionNumber, StringComparer.Ordinal).ThenBy(r => r.Id).ToArray();
        return new(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize);
    }
    public async Task<ProductTemplateDto> Detail(HttpContext http, long id, CancellationToken ct)
    {
        var row = (await Rows(http, "api/templates", ct)).FirstOrDefault(r => r.GetProperty("id").GetInt64() == id);
        return row.ValueKind == JsonValueKind.Object ? Dto(row) : throw new CrmFault(404, "Szablon nie znaleziony.");
    }
    public async Task<Page<TemplateProjectDto>> Projects(HttpContext http, string? q, int page, int pageSize, CancellationToken ct)
    {
        NativeTemplates.QueryValidation(q, null, null, page, pageSize);
        var rows = (await Rows(http, "api/projects", ct)).Select(r => new TemplateProjectDto(Text(r, "project_code") ?? "", r.GetProperty("positions_count").GetInt32()))
            .Where(r => string.IsNullOrWhiteSpace(q) || r.Code.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Code, StringComparer.Ordinal).ToArray();
        return new(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize);
    }
    public async Task<TemplateFeatures> Features(HttpContext http, CancellationToken ct)
    {
        var settings = await Rows(http, "api/settings", ct); var setting = settings.FirstOrDefault(s => Text(s, "key") == "labor_rate_pln");
        var raw = setting.ValueKind == JsonValueKind.Object ? Text(setting, "value") : null;
        var labor = double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate) && rate >= 0 ? rate : 90;
        return new(true, false, true, true, false, labor);
    }
    public async Task<IResult> Download(HttpContext http, string route, string filename, bool drawing, CancellationToken ct)
    {
        var bytes = await Fetch(http, route, drawing ? 25L * 1024 * 1024 : 100L * 1024 * 1024, true, ct);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new CrmFault(422, "Nie można odczytać poprawnego PDF.");
        return Results.File(bytes, "application/pdf", filename);
    }
}
