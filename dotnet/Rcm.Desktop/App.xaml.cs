using System.Windows;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Rcm.Desktop;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        var polish = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = polish;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = polish;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("pl-PL")));
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var startup = new StartupWindow(); startup.Show();
            var notice = await StartupUpdates.Run(startup);
            var exit = startup.ExitRequested;
            if (exit) { Shutdown(); return; }
            var server = DemoProfile.ServerEndpoint;
            var api = new CrmClient(server, savedLogins: new SavedLoginStore(server));
            var restored = false; var connectionNotice = "";
            startup.RestoreSession();
            try { restored = await api.RestoreLogin(startup.Lifetime.Token); }
            catch (Exception error) when (error is ApiFailure or HttpRequestException or OperationCanceledException)
            { connectionNotice = "Nie można przywrócić logowania. Sprawdź połączenie i wybierz „Zaloguj się”, aby spróbować ponownie."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            { connectionNotice = "Nie można odczytać zapamiętanego logowania. Zaloguj się ponownie."; }
            exit = startup.ExitRequested; startup.Complete();
            if (exit) { api.Dispose(); Shutdown(); return; }
            if (!restored)
            {
                var login = new LoginWindow(api);
                login.SetStartupNotice(notice); login.SetConnectionNotice(connectionNotice);
                if (login.ShowDialog() != true) { api.Dispose(); Shutdown(); return; }
            }
            var window = new MainWindow(api);
            MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose; window.Show();
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        { MessageBox.Show("Nieprawidłowy adres serwera. Sprawdź FACTORYFLOW_SERVER_URL (adres lokalny).", "FactoryFlow"); Shutdown(1); }
    }
}
