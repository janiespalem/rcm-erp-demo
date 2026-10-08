using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rcm.Updates;
using Velopack.Locators;
using Xunit;

namespace Rcm.Updates.Tests;

public sealed class InterruptedUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_update_preserves_installed_base_package_and_removes_rejected_downloads(bool disconnect)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rcm-interrupted-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var basePath = Path.Combine(directory, "RCM-0.2.0-full.nupkg");
        try
        {
            using (var archive = ZipFile.Open(basePath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("RCM.nuspec").Open()))
                writer.Write("<package><metadata><id>RCM</id><version>0.2.0</version><authors>RCM</authors><description>Synthetic base</description></metadata></package>");
            var original = await File.ReadAllBytesAsync(basePath);
            using var key = RSA.Create(2048);
            var package = "synthetic target"u8.ToArray();
            var feed = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Assets = new[] { new { PackageId = "RCM", Version = "0.2.1", Type = "Full", FileName = "RCM-0.2.1-full.nupkg", Size = package.Length, SHA256 = Convert.ToHexString(SHA256.HashData(package)) } } }));
            var signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var source = new SignedUpdateSource(new Uri("https://release.test/desktop/"), key.ExportSubjectPublicKeyInfoPem(), handler: new Failure(feed, signature, package, disconnect));
            var locator = new TestVelopackLocator("RCM", "0.2.0", directory);
            Assert.NotNull(locator.GetLatestLocalFullPackage());
            var manager = new VerifiedUpdateManager(source, locator);
            var update = await manager.CheckForUpdatesAsync(); Assert.NotNull(update);
            if (disconnect) await Assert.ThrowsAsync<HttpRequestException>(() => manager.DownloadUpdatesAsync(update));
            else await Assert.ThrowsAsync<CryptographicException>(() => manager.DownloadUpdatesAsync(update));
            Assert.True(File.Exists(basePath));
            Assert.Equal(original, await File.ReadAllBytesAsync(basePath));
            Assert.Equal("0.2.0", locator.GetLatestLocalFullPackage()!.Version.ToString());
            Assert.False(File.Exists(Path.Combine(directory, "RCM-0.2.1-full.nupkg")));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Failure(byte[] feed, byte[] signature, byte[] package, bool disconnect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.AbsolutePath;
            if (name.EndsWith(".nupkg") && disconnect) throw new HttpRequestException("Synthetic interrupted connection.");
            var content = name.EndsWith(".sig") ? signature : name.EndsWith(".json") ? feed : package.Select(b => (byte)(b ^ 1)).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
