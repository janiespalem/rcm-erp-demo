using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace Rcm.Desktop;
public partial class LoginWindow : Window
{
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public LoginWindow(CrmClient api)
    {
        this.api = api; InitializeComponent();
        Remember.IsChecked = api.RememberMe || api.HasSavedLogin;
        Loaded += (_, _) => Username.Focus(); Closed += (_, _) => lifetime.Cancel();
    }
    public void SetConnectionNotice(string text) => Status.Text = text;
    public void SetStartupNotice(string text)
    {
        StartupNotice.Text = text;
        StartupNotice.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!Submit.IsEnabled) return;
        Submit.IsEnabled = false; Remember.IsEnabled = false; Status.Text = "Logowanie…";
        var username = Username.Text.Trim();
        var secret = Password.Password;
        try
        {
            if (string.IsNullOrEmpty(secret) && api.HasSavedLogin && Remember.IsChecked == true)
            {
                if (!await api.RestoreLogin(lifetime.Token))
                { Status.Text = "Zapamiętane logowanie wygasło. Podaj nazwę użytkownika i hasło."; return; }
            }
            else await api.LoginWithPassword(username, secret, lifetime.Token, Remember.IsChecked == true);
            if (!lifetime.IsCancellationRequested) { Password.Clear(); DialogResult = true; }
        }
        catch (ApiFailure ex) { Status.Text = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { Status.Text = "Nie można połączyć się z serwerem. Sprawdź połączenie i spróbuj ponownie."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { Status.Text = "Nie można bezpiecznie zapamiętać logowania na tym komputerze. Spróbuj ponownie lub odznacz „Zapamiętaj mnie”."; }
        finally { secret = ""; Submit.IsEnabled = true; Remember.IsEnabled = true; Password.Focus(); }
    }
}
