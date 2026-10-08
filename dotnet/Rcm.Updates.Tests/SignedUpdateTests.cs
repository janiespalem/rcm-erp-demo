using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rcm.Updates;
using Velopack.Logging;
using Velopack.Locators;
using Xunit;
namespace Rcm.Updates.Tests;
public sealed class SignedUpdateTests
{
    private static readonly NullVelopackLogger Log = new();
    [Theory]
    [InlineData("Full")]
    [InlineData("Delta")]
    public async Task Signed_feed_and_package_are_verified_before_acceptance(string kind)
    {
        using var fixture = new Feed(kind: kind);
        using var source = fixture.Source();
        var feed = await source.GetReleaseFeed(Log, "RCM", "win");
        var file = Path.GetTempFileName();
        try { await source.DownloadReleaseEntry(Log, Assert.Single(feed.Assets), file, _ => { }); Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(file)); }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task Changed_feed_is_rejected_even_when_HTTPS_succeeds()
    {
        using var fixture = new Feed(); fixture.Json = fixture.Json.Replace("0.2.1", "0.2.2");
        using var source = fixture.Source();
        await Assert.ThrowsAsync<CryptographicException>(() => source.GetReleaseFeed(Log, "RCM", "win"));
    }
    [Theory]
    [InlineData("../RCM-full.nupkg")]
    [InlineData("https://other.test/RCM-full.nupkg")]
    [InlineData("RCM%2fother-full.nupkg")]
    public async Task Signed_feed_cannot_redirect_downloads_outside_the_release_directory(string name)
    {
        using var fixture = new Feed(name); using var source = fixture.Source();
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(Log, "RCM", "win"));
    }
    [Theory]
    [InlineData("Full")]
    [InlineData("Delta")]
    public async Task Corrupted_package_is_rejected_and_partial_file_removed(string kind)
    {
        using var fixture = new Feed(kind: kind); using var source = fixture.Source();
        var feed = await source.GetReleaseFeed(Log, "RCM", "win"); fixture.Package = "tampered package"u8.ToArray();
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".partial");
        await Assert.ThrowsAsync<CryptographicException>(() => source.DownloadReleaseEntry(Log, feed.Assets[0], file, _ => { }));
        Assert.False(File.Exists(file));
    }
    [Fact]
    public async Task Cancelled_startup_does_not_download_or_apply_anything()
    {
        using var fixture = new Feed(); using var ct = new CancellationTokenSource(); ct.Cancel();
        using var source = fixture.Source(ct.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetReleaseFeed(Log, "RCM", "win"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupted_cached_package_is_replaced_and_verified(bool lowercaseHash)
    {
        using var fixture = new Feed(lowercaseHash: lowercaseHash); using var source = fixture.Source();
        var directory = Path.Combine(Path.GetTempPath(), "rcm-update-"+Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.0", directory));
            var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
            var path = Path.Combine(directory, update.TargetFullRelease.FileName);
            await File.WriteAllTextAsync(path, "broken cached download");
            await manager.DownloadUpdatesAsync(update);
            Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Replayed_older_signed_feed_never_downgrades_the_application()
    {
        using var fixture = new Feed(); using var source = fixture.Source();
        var directory = Path.Combine(Path.GetTempPath(), "rcm-update-"+Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var manager = new VerifiedUpdateManager(source, new TestVelopackLocator("RCM", "0.2.2", directory));
            Assert.Null(await manager.CheckForUpdatesAsync());
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Feed : HttpMessageHandler
    {
        private readonly RSA key = RSA.Create(2048);
        public byte[] Package = "synthetic release"u8.ToArray();
        public string Json;
        private readonly byte[] signature;
        public Feed(string name = "RCM-0.2.1-full.nupkg", bool lowercaseHash = false, string kind = "Full")
        {
            if (kind == "Delta") name = name.Replace("-full.nupkg", "-delta.nupkg");
            Json = JsonSerializer.Serialize(new { Assets = new[] { new { PackageId = "RCM", Version = "0.2.1", Type = kind, FileName = name, Size = Package.Length, SHA256 = lowercaseHash ? Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant() : Convert.ToHexString(SHA256.HashData(Package)), SHA1 = Convert.ToHexString(SHA1.HashData(Package)) } } });
            signature = key.SignData(Encoding.UTF8.GetBytes(Json), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        public SignedUpdateSource Source(CancellationToken ct = default) => new(new Uri("https://release.test/desktop/"), key.ExportSubjectPublicKeyInfoPem(), ct, this);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(path.EndsWith(".sig") ? signature : path.EndsWith(".json") ? Encoding.UTF8.GetBytes(Json) : Package) });
        }
        protected override void Dispose(bool disposing) { if (disposing) key.Dispose(); base.Dispose(disposing); }
    }
}
