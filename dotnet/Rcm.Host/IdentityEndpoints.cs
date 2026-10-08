using Rcm.Contracts;

namespace Rcm.Host;

internal static class IdentityEndpoints
{
    public static void MapIdentityCompatibility(this WebApplication app)
    {
        app.MapGet("/api/auth/identity", async (HttpContext http, IIdentity identity, CancellationToken ct) =>
            await identity.Validate(http.Request.Headers.Authorization, ct));
        app.MapPost("/api/auth/login", async (HttpContext http, LoginRequest login, IIdentity identity, LoginAttempts attempts, CancellationToken ct) =>
        {
            if (app.Configuration["Identity:AllowPinLogin"] != "true") return Results.NotFound();
            return Results.Ok(await attempts.Authenticate(http, "pin:" + login.Role, () => identity.Login(login, ct)));
        });
        app.MapPost("/api/auth/login/password", async (HttpContext http, PasswordCredentials login, IIdentity identity, LoginAttempts attempts, CancellationToken ct) =>
        {
            string principal;
            try { principal = NativePasswords.NormalizeUsername(login.Username); }
            catch (ArgumentException) { principal = "invalid"; }
            return Results.Ok(await attempts.Authenticate(http, "password:" + principal,
                () => identity.LoginWithPassword(new(login.Username, login.Password, login.RememberMe), ct)));
        });
        app.MapPost("/api/auth/session/refresh", async (LegacyRefreshCredentials refresh, IIdentity identity, CancellationToken ct) =>
            Results.Ok(await identity.Refresh(new(refresh.RefreshToken), ct)));
        app.MapPost("/api/auth/session/logout", async (LegacyRefreshCredentials refresh, IIdentity identity, CancellationToken ct) =>
        {
            await identity.Logout(new(refresh.RefreshToken), ct);
            return Results.NoContent();
        });
    }
}
