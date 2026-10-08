using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class SessionCompatibilityTests
{
    [Theory]
    [InlineData(true, "{broken")]
    [InlineData(false, "{broken")]
    [InlineData(true, "{}")]
    [InlineData(false, "{}")]
    [InlineData(true, "{\"access_token\":null}")]
    public async Task Unreadable_login_or_identity_response_is_a_recoverable_failure(bool loginReply, string body)
    {
        var handler = new MalformedServer(loginReply, body);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var error = await Assert.ThrowsAsync<ApiFailure>(() => api.Login("biuro", "0000", default));
        Assert.Equal(502, error.Status);
        Assert.Null(api.Session);
        Assert.Equal(loginReply ? 1 : 2, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tetrapods\":null,\"baskets\":null,\"weightsKg\":null,\"parts\":[],\"diameters\":[]}")]
    public async Task Incomplete_calculation_response_is_not_reported_as_a_result(string body)
    {
        using var api = new CrmClient(new Uri("http://localhost/"), new MalformedServer(true, body));
        var error = await Assert.ThrowsAsync<ApiFailure>(() => api.CalculateTetrapod(new(1, 0), default));
        Assert.Equal(502, error.Status);
    }

    [Fact]
    public async Task Previous_backend_can_authenticate_existing_CRM_users_after_rollback()
    {
        var handler = new SessionServer(HttpStatusCode.NotFound);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("biuro", "0000", default);
        Assert.Equal(101, api.Session!.UserId);
        Assert.Equal(new[] { "/api/v1/session/login", "/api/v1/session", "/api/v1/crm/session" }, handler.Paths);
        Assert.Equal("synthetic-session", handler.SessionBearer);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Access_denials_and_outages_never_try_a_different_session_route(HttpStatusCode status)
    {
        var handler = new SessionServer(status);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var error = await Assert.ThrowsAsync<ApiFailure>(() => api.Login("biuro", "0000", default));
        Assert.Equal((int)status, error.Status);
        Assert.Null(api.Session);
        Assert.DoesNotContain("/api/v1/crm/session", handler.Paths);
    }

    private sealed class MalformedServer(bool loginReply, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = !loginReply && Calls == 1
                    ? JsonContent.Create(new { access_token = "synthetic-session" })
                    : new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SessionServer(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? SessionBearer { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/login", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "synthetic-session" }) });
            SessionBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(path == "/api/v1/session"
                ? new HttpResponseMessage(status) { Content = JsonContent.Create(new ApiError((int)status, "Synthetic session response")) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SessionDto(101, "Synthetic CRM user", Guid.NewGuid(), "Synthetic team", "biuro")) });
        }
    }
}
