using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rcm.Desktop;

public interface IBackgroundUpdateClient : IDisposable
{
    bool IsInstalled { get; }
    Task<string?> Check(CancellationToken ct);
    Task Download(Action<int> progress, CancellationToken ct);
    Task Apply(CancellationToken ct);
}

public sealed partial class BackgroundUpdates(IBackgroundUpdateClient client) : ObservableObject
{
    private int running;
    private long attempt;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool ready;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string version = "";
    [ObservableProperty] private bool restartPending;

    public async Task CheckAndDownload(CancellationToken ct)
    {
        if (Ready || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        var current = Interlocked.Increment(ref attempt);
        try
        {
            if (Ready || !client.IsInstalled) return;
            Busy = true; Status = ""; Version = "";
            ct.ThrowIfCancellationRequested();
            var available = await client.Check(ct);
            ct.ThrowIfCancellationRequested();
            if (available is null) return;
            if (string.IsNullOrWhiteSpace(available)) throw new InvalidDataException();
            Version = available;
            Status = $"Pobieranie aktualizacji {available}…";
            var progress = new Progress<int>(value =>
            {
                if (Busy && current == attempt && !ct.IsCancellationRequested)
                    Status = $"Pobieranie aktualizacji {available}: {Math.Clamp(value, 0, 100)}%";
            });
            await client.Download(value => ((IProgress<int>)progress).Report(value), ct);
            ct.ThrowIfCancellationRequested();
            Ready = true;
            Status = $"Aktualizacja {available} gotowa do instalacji.";
        }
        catch (OperationCanceledException) { Failed("Aktualizacja przerwana. Korzystasz z bieżącej wersji."); }
        catch (CryptographicException) { Failed("Nie potwierdzono podpisu aktualizacji. Korzystasz z bieżącej wersji."); }
        catch (Exception) { Failed("Aktualizacja chwilowo niedostępna. Korzystasz z bieżącej wersji."); }
        finally { Busy = false; Interlocked.Exchange(ref running, 0); }
    }

    public async Task<bool> ApplyIfSafe(bool safe, CancellationToken ct)
    {
        if (!Ready || Busy) return false;
        if (!safe)
        {
            Status = "Zapisz dane i zamknij otwarty formularz przed instalacją aktualizacji.";
            return false;
        }
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
        try
        {
            if (!Ready) return false;
            Busy = true;
            Status = "Sprawdzanie pakietu aktualizacji…";
            ct.ThrowIfCancellationRequested();
            await client.Apply(ct);
            ct.ThrowIfCancellationRequested();
            Ready = false;
            Status = "Instalowanie aktualizacji…";
            return true;
        }
        catch (OperationCanceledException)
        {
            Failed("Instalacja przerwana. Korzystasz z bieżącej wersji.");
            return false;
        }
        catch (Exception)
        {
            Failed("Nie udało się zainstalować aktualizacji. Korzystasz z bieżącej wersji.");
            return false;
        }
        finally { Busy = false; Interlocked.Exchange(ref running, 0); }
    }

    private void Failed(string message) { Ready = false; Version = ""; Status = message; }
}
