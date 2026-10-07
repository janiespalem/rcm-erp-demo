using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rcm.Host;

internal sealed class NativeIdentityJwt(string secret, TimeProvider clock)
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(secret);
    internal sealed record Claims(long UserId, string Method, int? PasswordVersion, string? SessionId);

    public string Issue(long userId, string method, int? passwordVersion = null, string? sessionId = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(CultureInfo.InvariantCulture),
            ["auth"] = method,
            ["exp"] = clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds()
        };
        if (passwordVersion is not null) payload["pwdv"] = passwordVersion.Value;
        if (sessionId is not null) payload["sid"] = sessionId;
        var unsigned = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return unsigned + "." + Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(unsigned)));
    }

    public Claims? Validate(string token)
    {
        if (token.Length is < 10 or > 4096) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var signature = Decode(parts[2]);
            if (signature.Length != 32 || !CryptographicOperations.FixedTimeEquals(signature,
                HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])))) return null;
            using var headerDoc = JsonDocument.Parse(Decode(parts[0]), new JsonDocumentOptions { MaxDepth = 8 });
            var header = headerDoc.RootElement;
            if (!UniqueObject(header) || !header.TryGetProperty("alg", out var algorithm) || algorithm.ValueKind != JsonValueKind.String || algorithm.GetString() != "HS256"
                || header.TryGetProperty("crit", out _) || header.TryGetProperty("b64", out _)
                || header.TryGetProperty("typ", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "JWT")) return null;
            using var payloadDoc = JsonDocument.Parse(Decode(parts[1]), new JsonDocumentOptions { MaxDepth = 8 });
            var payload = payloadDoc.RootElement;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (!UniqueObject(payload) || !payload.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
                || !long.TryParse(subject.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                || !Integer(payload, "exp", out var expires) || expires <= now
                || payload.TryGetProperty("nbf", out _) && (!Integer(payload, "nbf", out var notBefore) || notBefore > now)
                || payload.TryGetProperty("iat", out _) && (!Integer(payload, "iat", out var issued) || issued > now)
                || payload.TryGetProperty("aud", out _)) return null;
            var method = "pin";
            if (payload.TryGetProperty("auth", out var auth))
            {
                if (auth.ValueKind != JsonValueKind.String) return null;
                method = auth.GetString()!;
            }
            if (method is not ("pin" or "password" or "remembered_password")) return null;
            int? version = null;
            string? sessionId = null;
            if (method is "password" or "remembered_password")
            {
                if (!Integer(payload, "pwdv", out var pwdv) || pwdv < 0 || pwdv > int.MaxValue) return null;
                version = (int)pwdv;
            }
            if (method == "remembered_password")
            {
                if (!payload.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String || sid.GetString() is not { Length: > 0 and <= 36 } value) return null;
                sessionId = value;
            }
            return new(id, method, version, sessionId);
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || value.Length % 4 == 1 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new FormatException();
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (Encode(bytes) != value) throw new FormatException();
        return bytes;
    }
    private static bool UniqueObject(JsonElement obj) => obj.ValueKind == JsonValueKind.Object && obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == obj.EnumerateObject().Count();
    private static bool Integer(JsonElement obj, string property, out long number)
    {
        number = 0;
        return obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }
}
