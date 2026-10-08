using System.Net.Http.Headers;
using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LegacyCatalog(IHttpClientFactory clients)
{
    private async Task<JsonElement[]> List(HttpContext http, string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie można odczytać katalogu.");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
            var bytes = new byte[65536]; int read;
            while ((read = await input.ReadAsync(bytes, ct)) != 0)
            { if (buffer.Length + read > 8 * 1024 * 1024) throw new CrmFault(503, "Odpowiedź katalogu przekracza limit."); buffer.Write(bytes, 0, read); }
            return JsonSerializer.Deserialize<JsonElement[]>(buffer.ToArray()) ?? [];
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z katalogiem."); }
        catch (JsonException) { throw new CrmFault(503, "Nieprawidłowa odpowiedź katalogu."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas odczytu katalogu."); }
    }
    private static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Rate(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static void Validate(string? q, int page, int pageSize)
    { if (q?.Length > 200 || page is < 1 or > 1_000_000 || pageSize is < 1 or > 100) throw CrmFault.Invalid("page", "Nieprawidłowa strona katalogu."); }
    public async Task<Page<CatalogMaterialDto>> Materials(HttpContext http, string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize);
        var items = (await List(http, "api/approved-materials", ct)).Select(r => new CatalogMaterialDto(r.GetProperty("id").GetInt64(), r.GetProperty("name").GetString()!, Text(r, "category"), Rate(r, "default_rate_pln_kg"), r.GetProperty("is_active").GetBoolean(), Text(r, "notes"), 1))
            .Where(r => string.IsNullOrWhiteSpace(q) || (r.Name + " " + r.Category).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id).ToArray();
        return new(items.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), items.Length, page, pageSize);
    }
    public async Task<Page<CatalogOperationDto>> Operations(HttpContext http, string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize);
        var items = (await List(http, "api/operation-catalog/", ct)).Select(r => new CatalogOperationDto(r.GetProperty("id").GetInt64(), r.GetProperty("name").GetString()!, Text(r, "department"), Rate(r, "default_rate"), Text(r, "formula"), 1))
            .Where(r => string.IsNullOrWhiteSpace(q) || (r.Name + " " + r.Department + " " + r.Formula).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id).ToArray();
        return new(items.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), items.Length, page, pageSize);
    }
    public async Task<CatalogMaterialDto> Material(HttpContext http, long id, CancellationToken ct)
    {
        var rows = await List(http, "api/approved-materials", ct);
        var row = rows.FirstOrDefault(r => r.GetProperty("id").GetInt64() == id);
        if (row.ValueKind == JsonValueKind.Undefined) throw new CrmFault(404, "Materiał nie znaleziony.");
        return new(id, row.GetProperty("name").GetString()!, Text(row, "category"), Rate(row, "default_rate_pln_kg"), row.GetProperty("is_active").GetBoolean(), Text(row, "notes"), 1);
    }
    public async Task<CatalogOperationDto> Operation(HttpContext http, long id, CancellationToken ct)
    {
        var row = (await List(http, "api/operation-catalog/", ct)).FirstOrDefault(r => r.GetProperty("id").GetInt64() == id);
        if (row.ValueKind == JsonValueKind.Undefined) throw new CrmFault(404, "Operacja nie znaleziona.");
        return new(id, row.GetProperty("name").GetString()!, Text(row, "department"), Rate(row, "default_rate"), Text(row, "formula"), 1);
    }
    public async Task<CatalogOperationDto[]> Suggest(HttpContext http, string? text, string? material, CancellationToken ct)
    {
        if (text?.Length > 10000 || material?.Length > 1000) throw CrmFault.Invalid("text", "Opis jest zbyt długi.");
        var rows = await List(http, "api/operation-catalog/", ct);
        var all = rows.Select(r => new CatalogOperationDto(r.GetProperty("id").GetInt64(), r.GetProperty("name").GetString()!, Text(r, "department"), Rate(r, "default_rate"), Text(r, "formula"), 1));
        return CatalogKeywords.Suggest(all, text, material);
    }
    public async Task<CatalogFeatures> Features(HttpContext http, CancellationToken ct)
    {
        var settings = await List(http, "api/settings", ct);
        var value = settings.FirstOrDefault(r => Text(r, "key") == "labor_rate_pln");
        var text = value.ValueKind == JsonValueKind.Undefined ? null : Text(value, "value");
        var labor = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate) && rate >= 0 ? rate : 90;
        return new(true, true, false, labor);
    }
}
