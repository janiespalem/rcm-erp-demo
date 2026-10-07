using System.Net.Http.Headers;
using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LegacySettings(IHttpClientFactory clients)
{
    public SettingFeatures Features(LegacyUser actor) { NativeSettings.RequireRead(actor); return new(true, false); }
    public async Task<SettingDto[]> List(HttpContext http, LegacyUser actor, CancellationToken ct)
    {
        NativeSettings.RequireRead(actor);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/settings");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie można odczytać ustawień.");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
            byte[] buffer = new byte[65536]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            { if (output.Length + read > 1024 * 1024) throw new CrmFault(503, "Odpowiedź ustawień przekracza limit."); output.Write(buffer, 0, read); }
            using var json = JsonDocument.Parse(output.ToArray());
            return json.RootElement.EnumerateArray().Select(row => new SettingDto(row.GetProperty("key").GetString() ?? throw new JsonException(), row.GetProperty("value").GetString() ?? throw new JsonException(),
                row.TryGetProperty("label", out var label) && label.ValueKind != JsonValueKind.Null ? label.GetString() : null,
                row.TryGetProperty("version_id", out var version) ? version.GetInt64() : 1)).Where(row => !NativeSettings.Control(row.Key)).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z ustawieniami."); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new CrmFault(503, "Nieprawidłowa odpowiedź ustawień."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas odczytu ustawień."); }
    }
}
