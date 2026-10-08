using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Rcm.Updates;

public sealed class SignedUpdateSource : IUpdateSource, IDisposable
{
    private readonly HttpClient http;
    private readonly string publicKey;
    private readonly CancellationToken cancellation;
    private readonly Dictionary<string, (string Hash, long Size, string? ContentHash)> trusted = new(StringComparer.Ordinal);
    public SignedUpdateSource(Uri endpoint, string publicKey, CancellationToken cancellation = default, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)) throw new ArgumentException("Updates require HTTPS.");
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        this.publicKey = publicKey; this.cancellation = cancellation;
    }
    public static string ReleasePublicKey()
    {
        using var stream = typeof(SignedUpdateSource).Assembly.GetManifestResourceStream("Rcm.Updates.release-public.pem")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        trusted.Clear();
        if (channel != "win" || appId is not (null or "RCM")) throw new InvalidDataException("Unexpected update channel.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var bytes = await ReadBounded("releases.win.json", 1_048_576, timeout.Token);
        var signature = await ReadBounded("releases.win.json.sig", 1024, timeout.Token);
        using var key = RSA.Create(); key.ImportFromPem(publicKey);
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("Invalid release signature.");
        var json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var entries = document.RootElement.GetProperty("Assets").EnumerateArray().ToArray();
        var feed = VelopackAssetFeed.FromJson(json);
        foreach (var asset in feed.Assets)
        {
            var entry = entries.Single(item => item.GetProperty("FileName").GetString() == asset.FileName);
            string? contentHash = null;
            if (entry.TryGetProperty("ContentSHA256", out var content))
            {
                if (asset.Type != VelopackAssetType.Full || content.ValueKind != System.Text.Json.JsonValueKind.String
                    || !Regex.IsMatch(content.GetString()!, "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidDataException("Invalid package content digest.");
                contentHash = content.GetString()!.ToUpperInvariant();
            }
            if (asset.PackageId != "RCM" || asset.Version is null || !SafeName(asset.FileName) || asset.Size is <= 0 or > 536_870_912 ||
                asset.SHA256 is null || !Regex.IsMatch(asset.SHA256, "\\A[0-9a-fA-F]{64}\\z") ||
                !trusted.TryAdd(asset.FileName, (asset.SHA256, asset.Size, contentHash))) throw new InvalidDataException("Invalid release entry.");
            asset.SHA256 = asset.SHA256.ToUpperInvariant();
        }
        feed.Assets = feed.Assets.Where(a => a.Type is VelopackAssetType.Full or VelopackAssetType.Delta).ToArray();
        return feed;
    }
    public bool CanVerifyPackageContents(VelopackAsset asset) => trusted.TryGetValue(asset.FileName, out var entry) && entry.ContentHash is not null;
    public async Task VerifyPackageContentsAsync(VelopackAsset asset, string path, CancellationToken ct = default)
    {
        if (!trusted.TryGetValue(asset.FileName, out var entry) || entry.ContentHash is null)
            throw new CryptographicException("Package content digest is absent from the signed feed.");
        if (!string.Equals(await PackageContentDigest.CalculateAsync(path, ct), entry.ContentHash, StringComparison.Ordinal))
            throw new CryptographicException("Package contents differ from the signed feed.");
    }
    private static bool SafeName(string? name) => name is not null && name.Length <= 160 && Regex.IsMatch(name, "\\ARCM-[a-zA-Z0-9._-]+\\.(nupkg|exe|zip)\\z") && !name.Contains("..");
    private async Task<byte[]> ReadBounded(string name, int limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(name, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Release metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        { if (output.Length + read > limit) throw new InvalidDataException("Release metadata is too large."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        if (!trusted.TryGetValue(releaseEntry.FileName, out var expected)) throw new CryptographicException("Package is not in the verified release feed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        try
        {
            using var response = await http.GetAsync(releaseEntry.FileName, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > expected.Size) throw new CryptographicException("Package size differs from the signed feed.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress((int)(total * 100 / expected.Size));
                }
                if (total != expected.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Package differs from the signed feed.");
            }
        }
        catch { File.Delete(localFile); throw; }
    }
    public void Dispose() => http.Dispose();
}
