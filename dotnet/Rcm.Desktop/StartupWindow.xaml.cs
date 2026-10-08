using System.Windows;
namespace Rcm.Desktop;
public partial class StartupWindow : Window
{
    public CancellationTokenSource Cancellation { get; } = new();
    public CancellationTokenSource Lifetime { get; } = new();
    public bool ExitRequested { get; private set; }
    private bool completed;
    public StartupWindow()
    {
        InitializeComponent();
        Closing += (_, _) => { if (!completed) { ExitRequested = true; Cancellation.Cancel(); Lifetime.Cancel(); } };
    }
    public void Report(int value) { Progress.IsIndeterminate = false; Progress.Value = value; Status.Text = $"Pobieranie aktualizacji… {value}%"; }
    public void RestoreSession() { Status.Text = "Uruchamianie…"; Progress.IsIndeterminate = true; SkipUpdate.Visibility = Visibility.Collapsed; }
    public void Complete() { completed = true; Close(); Cancellation.Dispose(); Lifetime.Dispose(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { Status.Text = "Uruchamianie bieżącej wersji…"; Cancellation.Cancel(); }
}
