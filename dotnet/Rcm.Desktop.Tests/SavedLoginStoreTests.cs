using System.IO;
using System.Text;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class SavedLoginStoreTests
{
    [Fact]
    public void Windows_protects_device_token_and_new_application_instance_can_read_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rcm-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var endpoint = new Uri("http://127.0.0.1:18081/");
            var login = new SavedLogin("synthetic-secret-token", 101);
            new SavedLoginStore(endpoint, directory).Write(login);
            var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory)));
            Assert.DoesNotContain(login.RefreshToken, Encoding.UTF8.GetString(bytes));
            Assert.Equal(login, new SavedLoginStore(endpoint, directory).Read());
            Assert.Null(new SavedLoginStore(new Uri("http://127.0.0.1:18082/"), directory).Read());
            new SavedLoginStore(endpoint, directory).Clear();
            Assert.Null(new SavedLoginStore(endpoint, directory).Read());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Corrupted_saved_login_is_removed_and_password_login_remains_possible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rcm-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SavedLoginStore(new Uri("http://127.0.0.1:18081/"), directory);
            store.Write(new("synthetic-token", 101));
            File.WriteAllText(Assert.Single(Directory.GetFiles(directory)), "invalid protected data");
            Assert.Null(store.Read());
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
