using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class DemoIsolationTests
{
    [Fact]
    public void Demo_defaults_are_local_and_installation_identity_is_independent()
    {
        Assert.Equal("http://127.0.0.1:18081/", DemoProfile.ResolveEndpoint(null).AbsoluteUri);
        Assert.Equal("FactoryFlow", typeof(App).Assembly.GetName().Name);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryFlow"), DemoProfile.DataDirectory);
    }

    [Theory]
    [InlineData("https://external.invalid/")]
    [InlineData("http://192.0.2.1/")]
    [InlineData("file:///tmp/demo")]
    [InlineData("http://user@127.0.0.1/")]
    [InlineData("http://127.0.0.1/api/")]
    [InlineData("http://127.0.0.1/?redirect=external")]
    public void Every_client_rejects_nonlocal_or_ambiguous_server_addresses(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => DemoProfile.ResolveEndpoint(endpoint));
        Assert.Throws<ArgumentException>(() => new CrmClient(new Uri(endpoint)));
    }

    [Fact]
    public async Task Production_environment_cannot_enable_startup_or_background_updates()
    {
        using var trap = new TcpListener(IPAddress.Loopback, 0);
        trap.Start();
        var oldServer = Environment.GetEnvironmentVariable("RCM_SERVER_URL");
        var oldUpdates = Environment.GetEnvironmentVariable("RCM_UPDATE_URL");
        var oldDemo = Environment.GetEnvironmentVariable("FACTORYFLOW_SERVER_URL");
        try
        {
            var address = $"http://127.0.0.1:{((IPEndPoint)trap.LocalEndpoint).Port}/";
            Environment.SetEnvironmentVariable("RCM_SERVER_URL", address);
            Environment.SetEnvironmentVariable("RCM_UPDATE_URL", address);
            Environment.SetEnvironmentVariable("FACTORYFLOW_SERVER_URL", null);
            Assert.Equal("http://127.0.0.1:18081/", DemoProfile.ServerEndpoint.AbsoluteUri);
            Assert.Contains("wyłączone", await StartupUpdates.Run(null!));
            using var updates = new BackgroundUpdateClient(() => throw new InvalidOperationException("Restart forbidden"));
            Assert.False(updates.IsInstalled);
            Assert.Null(await updates.Check(default));
            await Assert.ThrowsAsync<NotSupportedException>(() => updates.Download(_ => { }, default));
            await Assert.ThrowsAsync<NotSupportedException>(() => updates.Apply(default));
            Assert.False(trap.Pending());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RCM_SERVER_URL", oldServer);
            Environment.SetEnvironmentVariable("RCM_UPDATE_URL", oldUpdates);
            Environment.SetEnvironmentVariable("FACTORYFLOW_SERVER_URL", oldDemo);
        }
    }

    [Fact]
    public async Task A_local_server_cannot_redirect_login_to_another_server()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var trap = new TcpListener(IPAddress.Loopback, 0);
        trap.Start();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var response = Task.Run(async () =>
        {
            var request = await server.GetContextAsync();
            request.Response.StatusCode = 302;
            request.Response.RedirectLocation = $"http://127.0.0.1:{((IPEndPoint)trap.LocalEndpoint).Port}/";
            request.Response.Close();
        });
        using var api = new CrmClient(new Uri($"http://127.0.0.1:{port}/"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ApiFailure>(() => api.LoginWithPassword("synthetic", "synthetic", timeout.Token));
        await response.WaitAsync(timeout.Token);
        Assert.False(trap.Pending());
    }

    [Fact]
    public void Remembered_login_cannot_read_or_overwrite_the_production_store_even_in_the_same_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "factoryflow-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var endpoint = DemoProfile.ResolveEndpoint(null);
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri));
            var productionPath = Path.Combine(directory, Convert.ToHexString(entropy) + ".bin");
            var original = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new SavedLogin("synthetic-production-token", 101)), entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(productionPath, original);
            var demo = new SavedLoginStore(endpoint, directory);
            Assert.Null(demo.Read());
            demo.Write(new SavedLogin("synthetic-demo-token", 202));
            Assert.Equal(202, demo.Read()!.UserId);
            demo.Clear();
            Assert.Equal(original, File.ReadAllBytes(productionPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
