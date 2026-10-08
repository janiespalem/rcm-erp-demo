using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rcm.Updates;
using Velopack.Locators;
using Velopack.Logging;
using Xunit;

namespace Rcm.Updates.Tests;

public sealed class PackageContentTests
{
    [Fact]
    public async Task Canonical_digest_matches_python_and_ignores_zip_compression_order_and_separator()
    {
        using var fixture = new PackageFixture();
        var original = Path.Combine(fixture.Directory, "original.zip");
        var rebuilt = Path.Combine(fixture.Directory, "rebuilt.zip");
        Write(original, new Dictionary<string, string> { ["lib/app/b.txt"] = "second", ["lib/app/a.txt"] = "first" }, CompressionLevel.NoCompression);
        Write(rebuilt, new Dictionary<string, string> { [@"lib\app\a.txt"] = "first", ["lib/app/b.txt"] = "second" }, CompressionLevel.Optimal);
        Assert.NotEqual(await File.ReadAllBytesAsync(original), await File.ReadAllBytesAsync(rebuilt));
        Assert.Equal("8417458DAA64C167F2B1B7C8FF29E00EB9B34706562ACE6CE719AF5A00AC0B0A", await PackageContentDigest.CalculateAsync(original));
        Assert.Equal(await PackageContentDigest.CalculateAsync(original), await PackageContentDigest.CalculateAsync(rebuilt));
    }

    [Fact]
    public async Task Reconstructed_cached_package_is_verified_again_on_next_launch_without_a_full_download()
    {
        using var fixture = new PackageFixture();
        await fixture.Prepare();
        Write(fixture.TargetPath, fixture.Contents.Reverse().ToDictionary(x => x.Key.Replace('/', '\\'), x => x.Value), CompressionLevel.Optimal);
        Assert.NotEqual(fixture.Published, await File.ReadAllBytesAsync(fixture.TargetPath));
        using var source = fixture.Source();
        for (var launch = 0; launch < 2; launch++)
        {
            var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
            var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
            await manager.DownloadUpdatesAsync(update);
            await manager.VerifyReadyUpdateAsync(update, CancellationToken.None);
        }
        Assert.Equal(0, fixture.PackageRequests);
        Assert.Equal(await PackageContentDigest.CalculateAsync(fixture.TargetPath), fixture.ContentHash);
    }

    [Theory]
    [InlineData("lib/app/unchanged-data.txt")]
    [InlineData("lib/app/Rcm.Desktop.exe")]
    public async Task Changed_or_unchanged_files_in_cached_reconstruction_are_rejected_and_replaced(string entry)
    {
        using var fixture = new PackageFixture(); await fixture.Prepare();
        var altered = new Dictionary<string, string>(fixture.Contents) { [entry] = "tampered data" };
        Write(fixture.TargetPath, altered, CompressionLevel.Optimal);
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        await manager.DownloadUpdatesAsync(update);
        Assert.Equal(1, fixture.PackageRequests);
        Assert.Equal(fixture.Published, await File.ReadAllBytesAsync(fixture.TargetPath));
    }

    [Fact]
    public async Task Rejected_reconstruction_and_failed_full_download_preserve_the_installed_base()
    {
        using var fixture = new PackageFixture(); await fixture.Prepare(); fixture.FailDownload = true;
        var baseBytes = await File.ReadAllBytesAsync(fixture.BasePath);
        var altered = new Dictionary<string, string>(fixture.Contents) { ["lib/app/unchanged-data.txt"] = "tampered base entry" };
        Write(fixture.TargetPath, altered, CompressionLevel.Optimal);
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.DownloadUpdatesAsync(update));
        Assert.Equal(baseBytes, await File.ReadAllBytesAsync(fixture.BasePath));
        Assert.False(File.Exists(fixture.TargetPath));
        Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.partial"));
    }

    [Fact]
    public async Task Old_signed_feed_without_content_digest_uses_only_full_even_when_a_delta_exists()
    {
        using var fixture = new PackageFixture(); await fixture.Prepare(includeContentHash: false, includeDelta: true);
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        Assert.Empty(update.DeltasToTarget);
        await manager.DownloadUpdatesAsync(update);
        Assert.Equal(1, fixture.PackageRequests);
        Assert.Equal(fixture.Published, await File.ReadAllBytesAsync(fixture.TargetPath));
        await manager.VerifyReadyUpdateAsync(update, CancellationToken.None);
        Assert.Equal(1, fixture.PackageRequests);
    }

    [Fact]
    public async Task Signed_raw_full_download_cannot_be_substituted_with_identical_recompressed_contents()
    {
        using var fixture = new PackageFixture(); await fixture.Prepare();
        var rebuilt = Path.Combine(fixture.Directory, "repacked.zip"); Write(rebuilt, fixture.Contents, CompressionLevel.Optimal);
        fixture.Served = await File.ReadAllBytesAsync(rebuilt);
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        await Assert.ThrowsAsync<CryptographicException>(() => manager.DownloadUpdatesAsync(update));
        Assert.False(File.Exists(fixture.TargetPath)); Assert.True(File.Exists(fixture.BasePath));
    }

    [Fact]
    public async Task Ready_verification_rejects_missing_target_without_selecting_base_or_downloading()
    {
        using var fixture = new PackageFixture(); await fixture.Prepare();
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        var baseBytes = await File.ReadAllBytesAsync(fixture.BasePath);
        await File.WriteAllBytesAsync(fixture.TargetPath, fixture.Published);
        await manager.VerifyReadyUpdateAsync(update, CancellationToken.None);
        File.Delete(fixture.TargetPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.VerifyReadyUpdateAsync(update, CancellationToken.None));
        Assert.Equal(0, fixture.PackageRequests);
        Assert.Equal(baseBytes, await File.ReadAllBytesAsync(fixture.BasePath));
        Assert.False(File.Exists(fixture.TargetPath));
    }

    [Theory]
    [InlineData("lib/app/Rcm.Desktop.exe")]
    [InlineData("lib/app/unchanged-data.txt")]
    public async Task Ready_verification_rejects_contents_modified_after_successful_download(string entry)
    {
        using var fixture = new PackageFixture(); await fixture.Prepare();
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        await manager.DownloadUpdatesAsync(update);
        await manager.VerifyReadyUpdateAsync(update, CancellationToken.None);
        var downloads = fixture.PackageRequests;
        Write(fixture.TargetPath, new Dictionary<string, string>(fixture.Contents) { [entry] = "Modified after download" }, CompressionLevel.Optimal);
        var changed = await File.ReadAllBytesAsync(fixture.TargetPath);
        await Assert.ThrowsAsync<CryptographicException>(() => manager.VerifyReadyUpdateAsync(update, CancellationToken.None));
        Assert.Equal(downloads, fixture.PackageRequests);
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.TargetPath));
    }

    [Fact]
    public async Task Ready_verification_respects_cancellation_without_removing_cached_update()
    {
        using var fixture = new PackageFixture(); await fixture.Prepare();
        await File.WriteAllBytesAsync(fixture.TargetPath, fixture.Published);
        using var source = fixture.Source();
        var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", fixture.Directory));
        var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.VerifyReadyUpdateAsync(update, cancelled.Token));
        Assert.Equal(0, fixture.PackageRequests);
        Assert.Equal(fixture.Published, await File.ReadAllBytesAsync(fixture.TargetPath));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("lib//empty")]
    [InlineData("lib/file:stream")]
    [InlineData("lib/file.")]
    [InlineData("lib/file ")]
    [InlineData("lib/CON.txt")]
    [InlineData("lib/file.__symlink")]
    [InlineData("lib/ż.txt")]
    public async Task Digest_rejects_unsafe_or_ambiguous_entries(string name)
    {
        using var fixture = new PackageFixture(); var path = Path.Combine(fixture.Directory, "unsafe.zip");
        Write(path, new Dictionary<string, string> { [name] = "value" });
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageContentDigest.CalculateAsync(path));
    }

    [Theory]
    [InlineData("LIB/app/File")]
    [InlineData(@"lib\app\file")]
    public async Task Digest_rejects_case_collisions_and_normalized_duplicates(string second)
    {
        using var fixture = new PackageFixture(); var path = Path.Combine(fixture.Directory, "duplicate.zip");
        Write(path, new Dictionary<string, string> { ["lib/app/file"] = "first", [second] = "second" });
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageContentDigest.CalculateAsync(path));
    }

    [Fact]
    public async Task Digest_rejects_symlinks_and_entry_count_above_limit()
    {
        using var fixture = new PackageFixture(); var path = Path.Combine(fixture.Directory, "symlink.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var writer = new StreamWriter(entry.Open()); writer.Write("target");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageContentDigest.CalculateAsync(path));
        File.Delete(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            for (var index = 0; index < 4097; index++) archive.CreateEntry($"file{index}");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageContentDigest.CalculateAsync(path));
    }

    private static void Write(string path, IReadOnlyDictionary<string, string> contents, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, value) in contents)
        { using var writer = new StreamWriter(archive.CreateEntry(name, compression).Open(), new UTF8Encoding(false)); writer.Write(value); }
    }

    private sealed class PackageFixture : HttpMessageHandler
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "rcm-package-content-" + Guid.NewGuid());
        public string TargetPath => Path.Combine(Directory, "RCM-0.2.1-full.nupkg");
        public string BasePath => Path.Combine(Directory, "RCM-0.2.0-full.nupkg");
        public Dictionary<string, string> Contents { get; } = new()
        {
            ["RCM.nuspec"] = "<package><metadata><id>RCM</id><version>0.2.1</version><authors>RCM</authors><description>Synthetic target</description></metadata></package>",
            ["lib/app/Rcm.Desktop.exe"] = "synthetic application", ["lib/app/unchanged-data.txt"] = "stable content",
            ["lib/app/release.json"] = "{\"version\":\"0.2.1\"}"
        };
        public byte[] Published { get; private set; } = [];
        public byte[]? Served { get; set; }
        public string ContentHash { get; private set; } = "";
        public bool FailDownload { get; set; }
        public int PackageRequests { get; private set; }
        private readonly RSA key = RSA.Create(2048);
        private byte[] feed = [], signature = [];
        public PackageFixture() => System.IO.Directory.CreateDirectory(Directory);
        public async Task Prepare(bool includeContentHash = true, bool includeDelta = false)
        {
            Write(BasePath, new Dictionary<string, string> { ["RCM.nuspec"] = Contents["RCM.nuspec"].Replace("0.2.1", "0.2.0") });
            var published = Path.Combine(Directory, "published.zip"); Write(published, Contents);
            Published = await File.ReadAllBytesAsync(published); ContentHash = await PackageContentDigest.CalculateAsync(published);
            var asset = new Dictionary<string, object?>
            {
                ["PackageId"] = "RCM", ["Version"] = "0.2.1", ["Type"] = "Full", ["FileName"] = "RCM-0.2.1-full.nupkg",
                ["Size"] = Published.Length, ["SHA256"] = Convert.ToHexString(SHA256.HashData(Published))
            };
            if (includeContentHash) asset["ContentSHA256"] = ContentHash;
            List<Dictionary<string, object?>> assets = [asset];
            if (includeDelta) assets.Add(new()
            {
                ["PackageId"] = "RCM", ["Version"] = "0.2.1", ["Type"] = "Delta", ["FileName"] = "RCM-0.2.1-delta.nupkg",
                ["Size"] = 1, ["SHA256"] = Convert.ToHexString(SHA256.HashData([1]))
            });
            feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = assets });
            signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        public SignedUpdateSource Source() => new(new Uri("https://release.test/desktop/"), key.ExportSubjectPublicKeyInfoPem(), handler: this);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".nupkg"))
            {
                PackageRequests++;
                if (FailDownload) throw new HttpRequestException("Synthetic failed fallback.");
            }
            var bytes = request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature : request.RequestUri.AbsolutePath.EndsWith(".json") ? feed : Served ?? Published;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { key.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            base.Dispose(disposing);
        }
    }
}
