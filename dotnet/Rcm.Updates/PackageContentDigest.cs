using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rcm.Updates;

public static class PackageContentDigest
{
    private const long MaximumUncompressedSize = 1024L * 1024 * 1024;
    private static readonly Regex Reserved = new(@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static async Task<string> CalculateAsync(string path, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("RCM package contents v1\0"u8);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > 4096) throw new InvalidDataException("Invalid package entry count.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<(string Name, ZipArchiveEntry Entry)> files = [];
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            if (directory) name = name[..^1];
            var parts = name.Split('/');
            if (name.Length is < 1 or > 1024 || name.Any(c => c is < ' ' or > '~')
                || name.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
                || parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || Reserved.IsMatch(part))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || name.EndsWith(".__symlink", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe package entry.");
            if (!names.Add(name)) throw new InvalidDataException("Duplicate or case-colliding package entry.");
            if (entry.Length < 0 || entry.Length > MaximumUncompressedSize - total) throw new InvalidDataException("Package expands beyond the limit.");
            total += entry.Length;
            if (directory && entry.Length > 0) throw new InvalidDataException("Directory entry contains data.");
            if (!directory) files.Add((name, entry));
        }
        var buffer = new byte[65536]; var header = new byte[8];
        foreach (var (name, entry) in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = entry.Open(); long length = 0; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                length += read;
                if (length > entry.Length) throw new InvalidDataException("Package entry size differs from metadata.");
                content.AppendData(buffer, 0, read);
            }
            if (length != entry.Length) throw new InvalidDataException("Incomplete package entry.");
            var bytes = Encoding.ASCII.GetBytes(name);
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length); hash.AppendData(header, 0, 4);
            hash.AppendData(bytes);
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)length); hash.AppendData(header);
            hash.AppendData(content.GetHashAndReset());
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
