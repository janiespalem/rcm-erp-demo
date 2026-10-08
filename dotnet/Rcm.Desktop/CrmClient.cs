using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed class ApiFailure(int status, string message, Dictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public Dictionary<string, string[]> Errors { get; } = errors ?? [];
}
public sealed class CrmClient : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private readonly HttpClient http;
    private readonly ISavedLoginStore? savedLogins;
    private readonly SemaphoreSlim renewal = new(1, 1);
    private SavedLogin? remembered;
    private int authenticationGeneration;
    private readonly HttpClient attachmentHttp;
    private string? token;
    public SessionDto? Session { get; private set; }
    public bool RememberMe => remembered is not null;
    public bool HasSavedLogin
    {
        get
        {
            try { return savedLogins?.Read() is { PendingRevocation: false }; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
    public event EventHandler? SessionChanged;
    public CrmClient(Uri endpoint, HttpMessageHandler? handler = null, ISavedLoginStore? savedLogins = null)
    {
        DemoProfile.ValidateEndpoint(endpoint);
        this.savedLogins = savedLogins;
        var transport = handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        http = new HttpClient(transport);
        attachmentHttp = new HttpClient(transport, disposeHandler: false) { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FactoryFlow", typeof(CrmClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        attachmentHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FactoryFlow", typeof(CrmClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        http.BaseAddress = endpoint; http.Timeout = TimeSpan.FromSeconds(20);
    }
    public async Task Login(string role, string pin, CancellationToken ct)
        => await Login("api/v1/session/login", new LoginRequest(role, pin), ct);
    public async Task LoginWithPassword(string username, string password, CancellationToken ct, bool rememberMe = false)
        => await Login("api/v1/session/login/password", new PasswordLoginRequest(username, password, rememberMe), ct, rememberMe);
    private async Task Login(string path, object credentials, CancellationToken ct, bool rememberMe = false)
    {
        await renewal.WaitAsync(ct);
        try
        {
            var generation = authenticationGeneration;
            var login = await Send<LoginReply>(HttpMethod.Post, path, credentials, ct, autoRenew: false);
            if (rememberMe && string.IsNullOrEmpty(login.RefreshToken))
                throw new ApiFailure(503, "Serwer nie obsługuje jeszcze zapamiętywania logowania. Spróbuj bez tej opcji.");
            var next = rememberMe ? new SavedLogin(login.RefreshToken!, login.Id) : null;
            var session = await ReadIdentity(login, Session?.UserId, next, generation, ct);
            if (next is not null) savedLogins?.Write(next);
            else savedLogins?.Clear();
            token = login.AccessToken; Session = session; remembered = next;
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { renewal.Release(); }
    }

    private async Task<SessionDto> ReadIdentity(LoginReply login, long? expectedUser, SavedLogin? next, int generation, CancellationToken ct)
    {
        if (generation != authenticationGeneration) throw new ApiFailure(401, "Sesja została zakończona. Zaloguj się ponownie.");
        if (string.IsNullOrEmpty(login.AccessToken)) throw new ApiFailure(502, "Nie można potwierdzić logowania.");
        SessionDto session;
        try { session = await Send<SessionDto>(HttpMethod.Get, "api/v1/session", null, ct, autoRenew: false, authorization: login.AccessToken); }
        catch (ApiFailure error) when (error.Status == 404)
        {
            session = await Send<SessionDto>(HttpMethod.Get, "api/v1/crm/session", null, ct, autoRenew: false, authorization: login.AccessToken);
        }
        if (expectedUser is not null && session.UserId != expectedUser || next is not null && session.UserId != next.UserId)
            throw new ApiFailure(403, "Otwarty formularz należy do poprzedniego użytkownika. Zaloguj się na to samo konto.");
        if (generation != authenticationGeneration)
            throw new ApiFailure(401, "Sesja została zakończona. Zaloguj się ponownie.");
        return session with { DefaultShift = session.DefaultShift ?? login.DefaultShift };
    }

    public async Task<bool> RestoreLogin(CancellationToken ct)
    {
        await renewal.WaitAsync(ct);
        try
        {
            var saved = savedLogins?.Read();
            if (saved is null) return false;
            if (saved.PendingRevocation)
            { await RevokeRemembered(saved, ct); return false; }
            try { await Renew(saved, ct); return true; }
            catch (ApiFailure error) when (error.Status is 401 or 403)
            { ClearRejectedLogin(saved); return false; }
        }
        finally { renewal.Release(); }
    }

    private async Task Renew(SavedLogin saved, CancellationToken ct)
    {
        var generation = authenticationGeneration;
        var reply = await Send<LoginReply>(HttpMethod.Post, "api/v1/session/refresh", new { refreshToken = saved.RefreshToken }, ct, autoRenew: false);
        // The device token is stable so a lost response cannot lose the remembered session.
        if (reply.RefreshToken != saved.RefreshToken)
            throw new ApiFailure(502, "Nie można potwierdzić zapamiętanego logowania.");
        var session = await ReadIdentity(reply, Session?.UserId ?? saved.UserId, saved, generation, ct);
        token = reply.AccessToken; Session = session; remembered = saved;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RenewAfterExpiry(string? previousToken, CancellationToken ct)
    {
        await renewal.WaitAsync(ct);
        try
        {
            if (token != previousToken && token is not null) return;
            if (remembered is null) throw new ApiFailure(401, "Zaloguj się ponownie. Formularz pozostaje otwarty.");
            try { await Renew(remembered, ct); }
            catch (ApiFailure error) when (error.Status is 401 or 403)
            {
                if (remembered is not null) ClearRejectedLogin(remembered);
                throw;
            }
        }
        finally { renewal.Release(); }
    }

    private void ClearRejectedLogin(SavedLogin rejected)
    {
        if (savedLogins?.Read() is { PendingRevocation: false } current && current.RefreshToken == rejected.RefreshToken)
            savedLogins.Clear();
        remembered = null;
    }

    public void Logout()
    {
        var saved = remembered ?? savedLogins?.Read();
        if (saved is not null) savedLogins?.Write(saved with { PendingRevocation = true });
        authenticationGeneration++;
        token = null; remembered = null; Session = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var saved = remembered ?? savedLogins?.Read();
        Logout();
        if (saved is not null) await RevokeRemembered(saved, ct);
    }

    private async Task RevokeRemembered(SavedLogin saved, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/session/logout")
            { Content = JsonContent.Create(new { refreshToken = saved.RefreshToken }) };
            using var response = await http.SendAsync(request, ct);
            await EnsureSuccess(response, ct);
            if (savedLogins?.Read()?.RefreshToken == saved.RefreshToken) savedLogins.Clear();
        }
        catch (Exception error) when (error is ApiFailure or HttpRequestException or OperationCanceledException) { }
    }
    public Task<LegoCatalog> ReadLegoCatalog(CancellationToken ct) => Send<LegoCatalog>(HttpMethod.Get, "api/v1/calculators/lego/catalog", null, ct);
    public Task<LegoPlan> CalculateLego(LegoInput input, CancellationToken ct) => Send<LegoPlan>(HttpMethod.Post, "api/v1/calculators/lego", input, ct);
    public Task<TetrapodPlan> CalculateTetrapod(TetrapodInput input, CancellationToken ct) =>
        Send<TetrapodPlan>(HttpMethod.Post, "api/v1/calculators/tetrapod", input, ct);
    public Task<T> Get<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/crm/" + path, null, ct);
    public Task<T> Save<T>(HttpMethod method, string path, object payload, CancellationToken ct) => Send<T>(method, "api/v1/crm/" + path, payload, ct);
    public Task<T> ReadOrders<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/orders" + path, null, ct);
    public Task<Page<OrderQuestionQueueRow>> ReadOrderQuestionQueue(string q, string status, int page, CancellationToken ct)
        => ReadOrders<Page<OrderQuestionQueueRow>>($"/questions?q={Uri.EscapeDataString(q)}&status={Uri.EscapeDataString(status)}&page={page}&pageSize=50", ct);
    public Task<T> WriteOrder<T>(HttpMethod method, string path, object payload, CancellationToken ct) => Send<T>(method, "api/v1/orders" + path, payload, ct);
    public Task<T> ReadInsights<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/insights" + path, null, ct);
    public Task DownloadInsightsXlsx(string destination, CancellationToken ct) => DownloadFile("api/v1/insights/export.xlsx", destination, ct,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", authenticationGeneration);
    public Task<T> ReadCatalog<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/catalog" + path, null, ct);
    public Task<T> WriteCatalog<T>(string path, object payload, CancellationToken ct) => Send<T>(HttpMethod.Post, "api/v1/catalog" + path, payload, ct);
    public Task<T> ReadShiftReports<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/shift-reports" + path, null, ct);
    public Task<T> WriteShiftReport<T>(string path, object payload, CancellationToken ct) => Send<T>(HttpMethod.Post, "api/v1/shift-reports" + path, payload, ct);
    public Task<T> ReadProduction<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/production" + path, null, ct);
    public Task<T> SaveProduction<T>(HttpMethod method, string path, object payload, CancellationToken ct) => Send<T>(method, "api/v1/production" + path, payload, ct);
    public Task<T> ReadTemplates<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, "api/v1/templates" + path, null, ct);
    public Task<T> WriteTemplate<T>(string path, object? payload, CancellationToken ct) => Send<T>(HttpMethod.Post, "api/v1/templates" + path, payload, ct);
    public Task<Page<TemplateProjectDto>> ReadTemplateProjects(string query, CancellationToken ct) => Send<Page<TemplateProjectDto>>(HttpMethod.Get, "api/v1/projects" + query, null, ct);
    public Task DownloadTemplate(long id, bool drawing, string destination, CancellationToken ct) => DownloadFile($"api/v1/templates/{id}/{(drawing ? "drawing" : "arkusz")}", destination, ct, "application/pdf");
    public Task DownloadTemplateProject(string code, string destination, CancellationToken ct) => DownloadFile($"api/v1/projects/{Uri.EscapeDataString(code)}/arkusze", destination, ct, "application/pdf");
    public async Task<ProductTemplateDto> UploadTemplateDrawing(long id, long version, Guid requestId, string filename, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > 25 * 1024 * 1024) throw new ApiFailure(413, "Rysunek przekracza limit 25 MiB.");
        async Task<HttpResponseMessage> Request()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/templates/{id}/drawing");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Request-Id", requestId.ToString("D"));
            request.Headers.Add("X-Expected-Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Filename", Uri.EscapeDataString(filename));
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/pdf");
            return await attachmentHttp.SendAsync(request, ct);
        }
        using var response = await SendWithRenewal(Request, ct);
        await EnsureSuccess(response, ct);
        try { return await response.Content.ReadFromJsonAsync<ProductTemplateDto>(ResponseJson, ct) ?? throw new ApiFailure(502, "Brak potwierdzenia zapisu rysunku. Ponów tę samą operację."); }
        catch (JsonException) { throw new ApiFailure(502, "Nie można odczytać potwierdzenia rysunku. Ponów tę samą operację."); }
    }
    public async Task<OrderAttachmentDto> UploadOrderAttachment(long orderId, string source, Guid requestId, CancellationToken ct)
    {
        async Task<HttpResponseMessage> Request()
        {
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (file.Length > 100L * 1024 * 1024) throw new ApiFailure(413, "Plik przekracza limit 100 MiB.");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"api/v1/orders/{orderId}/attachments?filename={Uri.EscapeDataString(Path.GetFileName(source))}&requestId={requestId:D}");
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StreamContent(file, 65536);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return await attachmentHttp.SendAsync(request, ct);
        }
        using var response = await SendWithRenewal(Request, ct);
        await EnsureSuccess(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<OrderAttachmentDto>(ResponseJson, ct)
                ?? throw new ApiFailure(502, "Serwer nie potwierdził zapisu. Ponów z tym samym plikiem.");
        }
        catch (JsonException) { throw new ApiFailure(502, "Nie można odczytać potwierdzenia. Ponów z tym samym plikiem."); }
    }
    public Task DownloadOrderAttachment(long orderId, long attachmentId, string destination, CancellationToken ct) =>
        DownloadFile($"api/v1/orders/{orderId}/attachments/{attachmentId}/download", destination, ct);
    public Task DownloadOrderDocument(long orderId, string kind, string destination, CancellationToken ct)
    {
        var contentType = kind switch
        {
            "arkusz" or "oferta" => "application/pdf",
            "operations" => "application/zip",
            _ => throw new ArgumentException("Nieznany rodzaj dokumentu.", nameof(kind))
        };
        return DownloadFile($"api/v1/orders/{orderId}/documents/{kind}", destination, ct, contentType);
    }
    private async Task DownloadFile(string path, string destination, CancellationToken ct, string? contentType = null, int? expectedIdentity = null)
    {
        const long limit = 100L * 1024 * 1024;
        using var response = await SendResponse(HttpMethod.Get, path, null, ct, true,
            HttpCompletionOption.ResponseHeadersRead, transport: contentType is null ? http : attachmentHttp);
        await EnsureSuccess(response, ct);
        var actualType = response.Content.Headers.ContentType?.MediaType;
        if (contentType == "application/pdf" && actualType == "text/html")
        {
            if (!Path.GetExtension(destination).Equals(".html", StringComparison.OrdinalIgnoreCase))
                throw new ApiFailure(422, "Serwer przygotował dokument HTML. Ponów pobieranie i wybierz typ „Dokument HTML” oraz rozszerzenie .html.");
        }
        else if (contentType is not null && actualType != contentType)
            throw new ApiFailure(502, "Serwer nie zwrócił oczekiwanego dokumentu. Spróbuj ponownie.");
        var length = response.Content.Headers.ContentLength;
        if (length > limit) throw new ApiFailure(413, "Plik przekracza limit 100 MiB.");
        var target = Path.GetFullPath(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".rcm-download-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long received = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    received += read;
                    if (received > limit) throw new ApiFailure(413, "Plik przekracza limit 100 MiB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (length is not null && length != received) throw new ApiFailure(502, "Pobieranie pliku zostało przerwane. Spróbuj ponownie.");
                if (contentType is not null && received == 0) throw new ApiFailure(502, "Serwer zwrócił pusty dokument. Spróbuj ponownie.");
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (expectedIdentity is not null && expectedIdentity != authenticationGeneration) throw new OperationCanceledException("Zmieniono sesję.");
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<T> Send<T>(HttpMethod method, string path, object? payload, CancellationToken ct, bool autoRenew = true, string? authorization = null)
    {
        using var response = await SendResponse(method, path, payload, ct, autoRenew, authorization: authorization);
        await EnsureSuccess(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ResponseJson, ct)
                ?? throw new ApiFailure(502, "Serwer nie zwrócił odpowiedzi. Wpisane wartości zachowano.");
        }
        catch (JsonException)
        {
            throw new ApiFailure(502, "Nie można odczytać odpowiedzi serwera. Wpisane wartości zachowano; spróbuj ponownie.");
        }
    }

    private async Task<HttpResponseMessage> SendResponse(HttpMethod method, string path, object? payload, CancellationToken ct,
        bool autoRenew, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, string? authorization = null, HttpClient? transport = null)
    {
        async Task<HttpResponseMessage> Request()
        {
            using var request = new HttpRequestMessage(method, path);
            var bearer = authorization ?? token;
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (payload is not null) request.Content = JsonContent.Create(payload, payload.GetType());
            return await (transport ?? http).SendAsync(request, completion, ct);
        }
        return await SendWithRenewal(Request, ct, autoRenew);
    }

    private async Task<HttpResponseMessage> SendWithRenewal(Func<Task<HttpResponseMessage>> request, CancellationToken ct, bool autoRenew = true)
    {
        var previousToken = token;
        var response = await request();
        if (autoRenew && remembered is not null && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await RenewAfterExpiry(previousToken, ct);
            return await request();
        }
        return response;
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        ApiError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiError>(ct); } catch (JsonException) { }
        throw new ApiFailure((int)response.StatusCode, error?.Title ?? "Serwer nie potwierdził operacji.", error?.Errors);
    }
    public void Dispose() { attachmentHttp.Dispose(); http.Dispose(); }
    private sealed record LoginReply([property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
        [property: JsonPropertyName("id")] long Id = 0,
        [property: JsonPropertyName("default_shift")] string? DefaultShift = null);
}
