using System.Net.Http;
using System.Windows;

namespace Rcm.Desktop;

public class ProductionEditorWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    internal CrmClient Api { get; }
    public ProductionEditorViewModel Model { get; }
    public ProductionEditorForm Form { get; }
    protected ProductionEditorWindow(CrmClient api, ProductionEditorViewModel model)
    {
        Api = api; Model = model; Model.RefreshAccess(api); DataContext = model;
        Style = (Style)FindResource("WindowStyle"); Title = model.Heading; Width = 750; Height = 850; MinWidth = 560; MinHeight = 500;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Form = new ProductionEditorForm(); Content = Form;
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj lub zatrzymaj oczekiwanie."; }
            else if (Model.Uncertain) { e.Cancel = true; Model.Status = "Wynik nieznany. Ponów tę samą operację przed zamknięciem. Formularz pozostaje otwarty."; }
            else if (Model.Dirty && MessageBox.Show(this, "Odrzucić niezapisane dane?", "Niezapisane dane produkcji", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; lifetime.Dispose(); };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.RefreshAccess(Api);
    public void Cancel() => operation?.Cancel();
    internal async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || lifetime.IsCancellationRequested) return; using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation = request;
        try { await action(request.Token); }
        catch (ApiFailure ex) { Model.Status = ex.Message + " Formularz zachowano."; if (ex.Status == 401) { new LoginWindow(Api) { Owner = this }.ShowDialog(); Model.RefreshAccess(Api); } }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Model.Status = "Nie otrzymano odpowiedzi. Formularz zachowano."; }
        finally { operation = null; }
    }
}
public sealed class ProductionContractWindow(CrmClient api, ProductionEditorViewModel model) : ProductionEditorWindow(api, model);
public sealed class SteelDeliveryWindow(CrmClient api, ProductionEditorViewModel model) : ProductionEditorWindow(api, model);
