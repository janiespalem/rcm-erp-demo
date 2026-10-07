using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Rcm.Crm;

namespace Rcm.Host;

public sealed record LegacyUser(long Id, string Name, string Role);
public sealed record LegacyLogin([property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("default_shift")] string? DefaultShift = null,
    [property: JsonPropertyName("token_type")] string TokenType = "bearer");
public sealed record PasswordCredentials(string Username, string Password,
    [property: JsonPropertyName("remember_me")] bool RememberMe);
public sealed record SessionRefreshRequest(string RefreshToken);
public sealed record LegacyRefreshCredentials([property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed class LegacyIdentity(IHttpClientFactory clients) : IIdentity
{
    public async Task<LegacyUser> Validate(string? authorization, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(bearer.Parameter) || bearer.Parameter.Length > 4096)
            throw new CrmFault(401, "Zaloguj się ponownie. Formularz pozostaje otwarty.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/identity");
        request.Headers.Authorization = bearer;
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new CrmFault(401, "Sesja wygasła. Zaloguj się ponownie.");
            if (!response.IsSuccessStatusCode) throw new CrmFault(503, "Logowanie jest chwilowo niedostępne.");
            var user = await response.Content.ReadFromJsonAsync<LegacyUser>(ct);
            return user is { Id: > 0, Name: not null } ? user : throw new CrmFault(503, "Nie można potwierdzić sesji.");
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Nie można połączyć się z usługą logowania."); }
        catch (System.Text.Json.JsonException) { throw new CrmFault(503, "Nie można potwierdzić sesji."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Usługa logowania nie odpowiada."); }
    }
    public async Task<LegacyLogin> Login(Rcm.Contracts.LoginRequest login, CancellationToken ct)
    {
        if (login.Role is not ("biuro" or "technolog" or "ceo" or "produkcja" or "crm") || login.Pin is null || login.Pin.Length != 4 || !login.Pin.All(char.IsAsciiDigit))
            throw CrmFault.Invalid("pin", "Podaj rolę i czterocyfrowy PIN.");
        try
        {
            using var response = await clients.CreateClient("legacy").PostAsJsonAsync("api/auth/login", login, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CrmFault(401, "Nieprawidłowy PIN lub rola.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new CrmFault(429, "Zbyt wiele prób. Spróbuj za minutę.");
            if (!response.IsSuccessStatusCode) throw new CrmFault(503, "Logowanie jest chwilowo niedostępne.");
            var result = await response.Content.ReadFromJsonAsync<LegacyLogin>(ct);
            return result is { Id: > 0, AccessToken.Length: > 0 } ? result : throw new CrmFault(503, "Nie można potwierdzić logowania.");
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Nie można połączyć się z usługą logowania."); }
        catch (System.Text.Json.JsonException) { throw new CrmFault(503, "Nie można potwierdzić logowania."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Usługa logowania nie odpowiada."); }
    }

    public async Task<LegacyLogin> LoginWithPassword(Rcm.Contracts.PasswordLoginRequest login, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(login.Username) || login.Username.Length > 64 || string.IsNullOrEmpty(login.Password) || login.Password.Length > 128)
            throw CrmFault.Invalid("form", "Podaj nazwę użytkownika i hasło.");
        try
        {
            var credentials = new PasswordCredentials(login.Username, login.Password, login.RememberMe);
            using var response = await clients.CreateClient("legacy").PostAsJsonAsync("api/auth/login/password", credentials, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CrmFault(401, "Nieprawidłowa nazwa użytkownika lub hasło.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new CrmFault(429, "Zbyt wiele prób. Spróbuj za minutę.");
            if (!response.IsSuccessStatusCode) throw new CrmFault(503, "Logowanie jest chwilowo niedostępne.");
            var result = await response.Content.ReadFromJsonAsync<LegacyLogin>(ct);
            return result is { Id: > 0, AccessToken.Length: > 0 } ? result : throw new CrmFault(503, "Nie można potwierdzić logowania.");
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Nie można połączyć się z usługą logowania."); }
        catch (System.Text.Json.JsonException) { throw new CrmFault(503, "Nie można potwierdzić logowania."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Usługa logowania nie odpowiada."); }
    }

    public async Task<LegacyLogin> Refresh(SessionRefreshRequest refresh, CancellationToken ct)
    {
        if (!ValidRefreshToken(refresh.RefreshToken)) throw new CrmFault(401, "Sesja wygasła. Zaloguj się ponownie.");
        try
        {
            using var response = await clients.CreateClient("legacy").PostAsJsonAsync("api/auth/session/refresh", new LegacyRefreshCredentials(refresh.RefreshToken), ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new CrmFault(401, "Sesja wygasła. Zaloguj się ponownie.");
            if (!response.IsSuccessStatusCode) throw new CrmFault(503, "Logowanie jest chwilowo niedostępne.");
            var result = await response.Content.ReadFromJsonAsync<LegacyLogin>(ct);
            return result is { Id: > 0, AccessToken.Length: > 0, RefreshToken.Length: > 0 } ? result : throw new CrmFault(503, "Nie można potwierdzić sesji.");
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Nie można połączyć się z usługą logowania."); }
        catch (System.Text.Json.JsonException) { throw new CrmFault(503, "Nie można potwierdzić sesji."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Usługa logowania nie odpowiada."); }
    }

    public async Task Logout(SessionRefreshRequest refresh, CancellationToken ct)
    {
        if (!ValidRefreshToken(refresh.RefreshToken)) return;
        try
        {
            using var response = await clients.CreateClient("legacy").PostAsJsonAsync("api/auth/session/logout", new LegacyRefreshCredentials(refresh.RefreshToken), ct);
            if (!response.IsSuccessStatusCode) throw new CrmFault(503, "Nie można potwierdzić wylogowania.");
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Nie można połączyć się z usługą logowania."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Usługa logowania nie odpowiada."); }
    }

    private static bool ValidRefreshToken(string? token) => token is { Length: 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
