using System.Net.Http;
using System.Windows;

namespace Rcm.Desktop;

public partial class CatalogEditorWindow : Window
{
    public CatalogEditorViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public CatalogEditorWindow(CrmClient api, CatalogEditorViewModel model)
    {
        this.api = api; Model = model; Model.RefreshAccess(api);
        InitializeComponent(); DataContext = Model; Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += (_, _) => { if (!Model.IsRemoval) CatalogName.Focus(); };
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj na potwierdzenie."; }
            else if (Model.Dirty && MessageBox.Show(this, Model.Uncertain
                ? "Wynik zapisu jest nieznany. Ponów ten sam zapis przed zamknięciem, aby uniknąć powtórzenia operacji. Zamknąć formularz?"
                : "Odrzucić niezapisane dane katalogu?", "Niezapisane dane katalogu", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.RefreshAccess(api);
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (ApiFailure ex)
        {
            if (lifetime.IsCancellationRequested) return;
            Model.Status = ex.Message + " Wpisane dane zachowano.";
            if (ex.Status == 401)
            {
                new LoginWindow(api) { Owner = this }.ShowDialog();
                Model.RefreshAccess(api); Model.Status = "Formularz zachowano. Po zalogowaniu ponów tę samą operację.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie otrzymano odpowiedzi. Wpisane dane zachowano."; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await Run(async () => { if (await Model.Save(api, lifetime.Token)) DialogResult = true; });
    private async void Compare_Click(object sender, RoutedEventArgs e) => await Run(() => Model.Compare(api, lifetime.Token));
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptComparison();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
