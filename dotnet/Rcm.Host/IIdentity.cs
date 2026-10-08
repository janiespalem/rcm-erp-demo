using Rcm.Contracts;

namespace Rcm.Host;

public interface IIdentity
{
    Task<LegacyUser> Validate(string? authorization, CancellationToken ct);
    Task<LegacyLogin> Login(LoginRequest login, CancellationToken ct);
    Task<LegacyLogin> LoginWithPassword(PasswordLoginRequest login, CancellationToken ct);
    Task<LegacyLogin> Refresh(SessionRefreshRequest refresh, CancellationToken ct);
    Task Logout(SessionRefreshRequest refresh, CancellationToken ct);
}
