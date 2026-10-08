using System.Text;
using System.Text.RegularExpressions;

namespace Rcm.Host;

public static partial class NativePasswords
{
    internal const string DummyHash = "$2b$12$u.a7IK6mU4d2RjL6CkwtQe5ZB7BM2uQw8bWP5caT5vW2VkF3OnrYa";

    [GeneratedRegex("\\A[a-z0-9][a-z0-9._-]{2,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string NormalizeUsername(string username)
    {
        if (username is null) throw new ArgumentException("Podaj nazwę użytkownika.");
        var normalized = username.Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(normalized))
            throw new ArgumentException("Nazwa użytkownika musi mieć 3–64 znaki: litery a-z, cyfry, kropka, myślnik lub podkreślenie.");
        return normalized;
    }

    public static void ValidatePassword(string password, string username)
    {
        if (password is null) throw new ArgumentException("Podaj hasło.");
        if (password.EnumerateRunes().Count() < 15)
            throw new ArgumentException("Hasło musi mieć co najmniej 15 znaków.");
        if (Encoding.UTF8.GetByteCount(password) > 72)
            throw new ArgumentException("Hasło jest za długie.");
        if (password != password.Trim())
            throw new ArgumentException("Hasło nie może zaczynać się ani kończyć spacją.");
        if (password.Contains(NormalizeUsername(username), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Hasło nie może zawierać nazwy użytkownika.");
    }

    public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(12, 'b'));

    internal static bool Matches(string password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (ArgumentException) { return false; }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
}
