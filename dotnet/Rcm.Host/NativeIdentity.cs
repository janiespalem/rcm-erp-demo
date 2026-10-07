using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

public sealed class NativeIdentity(IConfiguration configuration, TimeProvider clock) : IIdentity
{
    private const string AllowedRoles = "('biuro','technolog','ceo','produkcja','crm')";
    private const string UserColumns = "id,name,role::text,pin_hash,username,password_hash,password_version,default_shift";
    private readonly NativeIdentityJwt jwt = new(configuration["Identity:JwtSecret"]
        is { Length: > 0 } secret ? secret : throw new InvalidOperationException("Identity:JwtSecret is required."), clock);
    private sealed record User(long Id, string Name, string Role, string? PinHash, string? Username, string? PasswordHash, int PasswordVersion, string? DefaultShift);
    private sealed record Remembered(string Id, long UserId, int PasswordVersion, DateTime ExpiresAt, DateTime? RevokedAt);

    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var command = new NpgsqlCommand("""
            SELECT NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
              AND has_function_privilege(current_user,'public.require_identity_writer(text)','EXECUTE')
              AND (SELECT bool_and(has_column_privilege(current_user,'public.users',c,'SELECT'))
                FROM unnest(ARRAY['id','name','role','pin_hash','username','password_hash','password_version','default_shift']) c)
              AND NOT has_any_column_privilege(current_user,'public.users','INSERT,UPDATE')
              AND NOT has_table_privilege(current_user,'public.users','DELETE,TRUNCATE')
              AND NOT has_any_column_privilege(current_user,'public.settings','INSERT,UPDATE')
              AND NOT has_table_privilege(current_user,'public.settings','DELETE,TRUNCATE')
              AND has_table_privilege(current_user,'public.remembered_sessions','SELECT')
              AND (SELECT bool_and(has_column_privilege(current_user,'public.remembered_sessions',c,'INSERT'))
                FROM unnest(ARRAY['id','token_hash','user_id','password_version','created_at','last_used_at','expires_at']) c)
              AND (SELECT bool_and(has_column_privilege(current_user,'public.remembered_sessions',c,'UPDATE'))
                FROM unnest(ARRAY['last_used_at','expires_at','revoked_at']) c)
              AND NOT has_table_privilege(current_user,'public.remembered_sessions','DELETE,TRUNCATE')
              AND NOT has_column_privilege(current_user,'public.remembered_sessions','user_id','UPDATE')
              AND NOT has_column_privilege(current_user,'public.remembered_sessions','password_version','UPDATE')
              AND NOT has_column_privilege(current_user,'public.remembered_sessions','id','UPDATE')
              AND NOT has_column_privilege(current_user,'public.remembered_sessions','token_hash','UPDATE')
              AND NOT has_column_privilege(current_user,'public.remembered_sessions','created_at','UPDATE')
              AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname IN ('public','crm') AND c.relkind IN ('r','p')
                  AND NOT (n.nspname='public' AND c.relname='remembered_sessions')
                  AND (has_any_column_privilege(current_user,c.oid,'INSERT,UPDATE')
                    OR has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE')))
            FROM pg_roles r WHERE r.rolname=current_user
            """, db);
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new CrmFault(503, "Skonfiguruj ograniczone uprawnienia modułu logowania.");
    }

    public async Task<LegacyUser> Validate(string? authorization, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer)
            || !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(bearer.Parameter) || jwt.Validate(bearer.Parameter) is not { } claims)
            throw Expired();
        await using var db = await Open(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT id,name,role::text FROM public.users u
            WHERE u.id=@id AND u.role::text IN {AllowedRoles} AND (
              (@method='pin' AND u.pin_hash IS NOT NULL)
              OR (@method='password' AND u.password_hash IS NOT NULL AND u.password_version=@version)
              OR (@method='remembered_password' AND u.password_hash IS NOT NULL AND u.password_version=@version
                AND EXISTS(SELECT 1 FROM public.remembered_sessions s WHERE s.id=@sid AND s.user_id=u.id
                  AND s.password_version=u.password_version AND s.revoked_at IS NULL AND s.expires_at>@now)))
            """, db);
        command.Parameters.AddWithValue("id", claims.UserId);
        command.Parameters.AddWithValue("method", claims.Method);
        command.Parameters.Add(P("version", claims.PasswordVersion, NpgsqlDbType.Integer));
        command.Parameters.Add(P("sid", claims.SessionId, NpgsqlDbType.Varchar));
        command.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Expired();
        return new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task<LegacyLogin> Login(LoginRequest login, CancellationToken ct)
    {
        if (login is null || !ActiveRole(login.Role) || login.Pin is not { Length: 4 } || !login.Pin.All(char.IsAsciiDigit))
            throw CrmFault.Invalid("pin", "Podaj rolę i czterocyfrowy PIN.");
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var users = await Users(db, $"SELECT {UserColumns} FROM public.users WHERE role::text=@role AND pin_hash IS NOT NULL AND role::text IN {AllowedRoles} ORDER BY id", ct, P("role", login.Role));
        var user = users.FirstOrDefault(u => NativePasswords.Matches(login.Pin, u.PinHash!));
        if (user is null) throw new CrmFault(401, "Nieprawidłowy PIN lub rola.");
        var result = Result(user, "pin");
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<LegacyLogin> LoginWithPassword(PasswordLoginRequest login, CancellationToken ct)
    {
        if (login is null || string.IsNullOrWhiteSpace(login.Username) || login.Username.Length > 64 || string.IsNullOrEmpty(login.Password) || login.Password.EnumerateRunes().Count() > 128)
            throw CrmFault.Invalid("form", "Podaj nazwę użytkownika i hasło.");
        string normalized;
        try { normalized = NativePasswords.NormalizeUsername(login.Username); }
        catch (ArgumentException) { normalized = "invalid"; }
        var password = Encoding.UTF8.GetByteCount(login.Password) > 72 ? "invalid" : login.Password;
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var user = (await Users(db, $"SELECT {UserColumns} FROM public.users WHERE username=@username AND role::text IN {AllowedRoles} AND password_hash IS NOT NULL", ct, P("username", normalized))).SingleOrDefault();
        var matches = NativePasswords.Matches(password, user?.PasswordHash ?? NativePasswords.DummyHash);
        if (user is null || !matches) throw new CrmFault(401, "Nieprawidłowa nazwa użytkownika lub hasło.");
        if (!login.RememberMe)
        {
            var result = Result(user, "password", user.PasswordVersion);
            await tx.CommitAsync(ct);
            return result;
        }
        var sessionId = Guid.NewGuid().ToString();
        var refreshToken = NativeIdentityJwt.Encode(RandomNumberGenerator.GetBytes(48));
        var now = clock.GetUtcNow().UtcDateTime;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO public.remembered_sessions(id,token_hash,user_id,password_version,created_at,last_used_at,expires_at)
            SELECT @sid,@hash,id,password_version,@now,@now,@expires FROM public.users
            WHERE id=@id AND password_hash=@password_hash AND password_version=@version AND role::text IN {AllowedRoles}
            """, db);
        insert.Parameters.AddWithValue("sid", sessionId);
        insert.Parameters.AddWithValue("hash", HashRefresh(refreshToken)!);
        insert.Parameters.AddWithValue("id", user.Id);
        insert.Parameters.AddWithValue("password_hash", user.PasswordHash!);
        insert.Parameters.AddWithValue("version", user.PasswordVersion);
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("expires", now.AddDays(90));
        if (await insert.ExecuteNonQueryAsync(ct) != 1) throw Expired();
        var remembered = Result(user, "remembered_password", user.PasswordVersion, sessionId, refreshToken);
        await tx.CommitAsync(ct);
        return remembered;
    }

    public async Task<LegacyLogin> Refresh(SessionRefreshRequest refresh, CancellationToken ct)
    {
        var hash = HashRefresh(refresh?.RefreshToken);
        if (hash is null) throw Expired();
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var session = await Session(db, hash, ct);
        if (session is null) throw Expired();
        var now = clock.GetUtcNow().UtcDateTime;
        var user = (await Users(db, $"SELECT {UserColumns} FROM public.users WHERE id=@id AND role::text IN {AllowedRoles}", ct, P("id", session.UserId))).SingleOrDefault();
        if (session.RevokedAt is not null || session.ExpiresAt <= now || user?.PasswordHash is null || user.PasswordVersion != session.PasswordVersion)
        {
            await Revoke(db, session.Id, now, ct);
            await tx.CommitAsync(ct);
            throw Expired();
        }
        await using var update = new NpgsqlCommand("UPDATE public.remembered_sessions SET last_used_at=@now,expires_at=@expires WHERE id=@sid", db);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("expires", now.AddDays(90));
        update.Parameters.AddWithValue("sid", session.Id);
        await update.ExecuteNonQueryAsync(ct);
        var result = Result(user, "remembered_password", user.PasswordVersion, session.Id, refresh?.RefreshToken);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task Logout(SessionRefreshRequest refresh, CancellationToken ct)
    {
        var hash = HashRefresh(refresh?.RefreshToken);
        if (hash is null) return;
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var session = await Session(db, hash, ct);
        if (session is not null) await Revoke(db, session.Id, clock.GetUtcNow().UtcDateTime, ct);
        await tx.CommitAsync(ct);
    }

    private LegacyLogin Result(User user, string method, int? version = null, string? sessionId = null, string? refreshToken = null)
        => new(jwt.Issue(user.Id, method, version, sessionId), user.Id, user.Name, refreshToken, user.Role, user.DefaultShift);

    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("Identity") ?? throw new CrmFault(503, "Logowanie nie zostało skonfigurowane."));
        try { await db.OpenAsync(ct); return db; }
        catch (NpgsqlException) { await db.DisposeAsync(); throw new CrmFault(503, "Logowanie jest chwilowo niedostępne."); }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async Task EnsureWriter(NpgsqlConnection db, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SELECT public.require_identity_writer('dotnet')", db);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState is "55000" or "55P03")
        { throw new CrmFault(503, "Logowanie jest chwilowo niedostępne."); }
    }

    private static async Task<List<User>> Users(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var users = new List<User>();
        while (await reader.ReadAsync(ct)) users.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            Text(reader, 3), Text(reader, 4), Text(reader, 5), reader.GetInt32(6), Text(reader, 7)));
        return users;
    }

    private static async Task<Remembered?> Session(NpgsqlConnection db, string hash, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT id,user_id,password_version,expires_at,revoked_at FROM public.remembered_sessions WHERE token_hash=@hash FOR UPDATE", db);
        command.Parameters.AddWithValue("hash", hash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4)) : null;
    }

    private static async Task Revoke(NpgsqlConnection db, string id, DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE public.remembered_sessions SET revoked_at=@now WHERE id=@id AND revoked_at IS NULL", db);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static bool ActiveRole(string? role) => role is "biuro" or "technolog" or "ceo" or "produkcja" or "crm";
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType type = NpgsqlDbType.Unknown) => value is null ? new(name, type) { Value = DBNull.Value } : new(name, value);
    private static string? HashRefresh(string? token) => token is { Length: 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token))) : null;
    private static CrmFault Expired() => new(401, "Sesja wygasła. Zaloguj się ponownie.");
}
