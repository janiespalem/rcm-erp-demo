using System.IO;
using System.Text.Json;

namespace Rcm.Desktop;

public sealed record LegoCartEntry(Guid Id, string Kind, string Name, string Details, decimal UnitWeightT, decimal UnitKnownNetPln, bool Complete, string[] Warnings, int Quantity);
public interface ILegoCartStore
{
    Task<LegoCartEntry[]> Read(long userId, CancellationToken ct);
    Task Write(long userId, LegoCartEntry[] entries, CancellationToken ct);
}
public sealed class LegoCartStore(string? directory = null) : ILegoCartStore
{
    private readonly string directory = directory ?? Path.Combine(DemoProfile.DataDirectory, "lego-cart");
    private const int MaxBytes = 1024 * 1024;
    private string FilePath(long userId) => userId > 0 ? Path.Combine(directory, $"user-{userId}.json") : throw new ArgumentOutOfRangeException(nameof(userId));
    private static void Validate(LegoCartEntry[] entries)
    {
        if (entries.Length > 100 || entries.Select(row => row.Id).Distinct().Count() != entries.Length || entries.Any(row => row is null || row.Id == Guid.Empty || string.IsNullOrWhiteSpace(row.Kind) || row.Kind.Length > 100 || string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 300 || row.Details is null || row.Details.Length > 2000 || row.Quantity is < 1 or > 999 || row.UnitWeightT is < 0 or > 1_000_000_000 || row.UnitKnownNetPln is < 0 or > 1_000_000_000_000 || row.Warnings is null || row.Warnings.Length > 30 || row.Warnings.Any(text => text is null || text.Length > 4000)))
            throw new InvalidDataException("Nieprawidłowa zawartość lokalnego koszyka LEGO.");
    }
    public async Task<LegoCartEntry[]> Read(long userId, CancellationToken ct)
    {
        var path = FilePath(userId); if (!File.Exists(path)) return [];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > MaxBytes) throw new InvalidDataException("Lokalny koszyk jest zbyt duży.");
        using var buffer = new MemoryStream(); var bytes = new byte[65536]; int read;
        while ((read = await file.ReadAsync(bytes, ct)) != 0) { if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Lokalny koszyk jest zbyt duży."); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
        var entries = JsonSerializer.Deserialize<LegoCartEntry[]>(buffer.ToArray()) ?? throw new InvalidDataException("Nie można odczytać lokalnego koszyka."); Validate(entries); return entries;
    }
    public async Task Write(long userId, LegoCartEntry[] entries, CancellationToken ct)
    {
        Validate(entries); var path = FilePath(userId); var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Koszyk przekracza limit zapisu.");
        Directory.CreateDirectory(directory); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous)) { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
