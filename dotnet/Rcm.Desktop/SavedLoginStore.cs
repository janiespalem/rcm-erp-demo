using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rcm.Desktop;

public sealed record SavedLogin(string RefreshToken, long UserId, bool PendingRevocation = false);

public interface ISavedLoginStore
{
    SavedLogin? Read();
    void Write(SavedLogin login);
    void Clear();
}

public sealed class SavedLoginStore : ISavedLoginStore
{
    private readonly string path;
    private readonly byte[] entropy;

    public SavedLoginStore(Uri endpoint, string? directory = null)
    {
        entropy = SHA256.HashData(Encoding.UTF8.GetBytes("FactoryFlow:" + endpoint.AbsoluteUri));
        directory ??= Path.Combine(DemoProfile.DataDirectory, "identity");
        path = Path.Combine(directory, Convert.ToHexString(entropy) + ".bin");
    }

    public SavedLogin? Read()
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 16384) { Clear(); return null; }
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            try
            {
                var login = JsonSerializer.Deserialize<SavedLogin>(bytes);
                if (login is { UserId: > 0, RefreshToken.Length: > 0 and <= 4096 }) return login;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is CryptographicException or JsonException) { }
        Clear();
        return null;
    }

    public void Write(SavedLogin login)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(login);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(protectedBytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear() => File.Delete(path);
}
