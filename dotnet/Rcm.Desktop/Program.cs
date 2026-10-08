using System.IO;
using System.Windows;
using System.Security.Cryptography;
using System.Text;
using Velopack;

namespace Rcm.Desktop;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var install = Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(install)))[..24];
        using var instance = new Mutex(false, @"Global\FactoryFlow." + id);
        bool acquired;
        try { acquired = instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { MessageBox.Show("FactoryFlow jest już uruchomiony. Wróć do otwartego okna.", "FactoryFlow"); return; }
        try { var app = new App(); app.InitializeComponent(); app.Run(); }
        finally { instance.ReleaseMutex(); }
    }
}
