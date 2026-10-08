using System.IO;
using System.Text.Json;

namespace Rcm.Desktop;

public sealed record TetrapodDelivery(Guid Id, DateTimeOffset ReceivedAt, double Kg6, double Kg12, double Kg16);
public interface ITetrapodDeliveryStore
{
    Task<TetrapodDelivery[]> Read(long userId, CancellationToken ct);
    Task Write(long userId, TetrapodDelivery[] entries, CancellationToken ct);
}
public sealed class TetrapodDeliveryStore(string? directory = null) : ITetrapodDeliveryStore
{
    private readonly string directory = directory ?? Path.Combine(DemoProfile.DataDirectory, "tetrapod-deliveries");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxBytes = 1024 * 1024;
    private string FilePath(long userId) => userId > 0 ? Path.Combine(directory, $"user-{userId}.json") : throw new ArgumentOutOfRangeException(nameof(userId));
    private static void Validate(TetrapodDelivery[] entries)
    {
        if (entries.Length > 5000 || entries.Any(row => row is null || row.Id == Guid.Empty || row.ReceivedAt == default ||
            !Valid(row.Kg6) || !Valid(row.Kg12) || !Valid(row.Kg16) || row.Kg6 + row.Kg12 + row.Kg16 <= 0) ||
            entries.Select(row => row.Id).Distinct().Count() != entries.Length)
            throw new InvalidDataException("Nieprawidłowa zawartość lokalnej historii dostaw.");
    }
    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and <= 1_000_000_000;
    public async Task<TetrapodDelivery[]> Read(long userId, CancellationToken ct)
    {
        var path = FilePath(userId); if (!File.Exists(path)) return [];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > MaxBytes) throw new InvalidDataException("Lokalna historia dostaw jest zbyt duża.");
        using var buffer = new MemoryStream(); var bytes = new byte[65536]; int read;
        while ((read = await file.ReadAsync(bytes, ct)) != 0) { if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Lokalna historia dostaw jest zbyt duża."); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
        var entries = JsonSerializer.Deserialize<TetrapodDelivery[]>(buffer.ToArray(), Json) ?? throw new InvalidDataException("Nie można odczytać lokalnej historii dostaw."); Validate(entries); return entries;
    }
    public async Task Write(long userId, TetrapodDelivery[] entries, CancellationToken ct)
    {
        Validate(entries); var path = FilePath(userId); var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Historia dostaw przekracza limit zapisu.");
        Directory.CreateDirectory(directory); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous)) { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
