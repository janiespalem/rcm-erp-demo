using System.Net.Http;
using System.Windows;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderEditorWindow : Window
{
    public OrderEditorViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public OrderEditorWindow(CrmClient api, OrderEditorViewModel model)
    {
        this.api = api; Model = model;
        InitializeComponent(); Types.ItemsSource = OrderLabels.Types; DataContext = model;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += async (_, _) => { ClientName.Focus(); await LoadLookups(); };
        Closing += (_, e) =>
        {
            if (Model.Saving) { e.Cancel = true; Model.Status = "Zapisywanie trwa. Poczekaj na odpowiedź."; }
            else if (Model.Saved is null && Model.Dirty && MessageBox.Show(this,
                Model.Uncertain ? "Zapis mógł się zakończyć. Zamknąć formularz bez potwierdzenia wyniku?" : "Odrzucić niezapisane dane zlecenia?",
                "Niezapisane dane", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => lifetime.Cancel();
    }
    private async Task LoadLookups()
    {
        try
        {
            var lookups = await api.ReadOrders<OrderLookups>("/lookups", lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var template = Model.TemplateId; var material = Model.ApprovedMaterialId;
            Templates.ItemsSource = lookups.Templates; ApprovedMaterials.ItemsSource = lookups.Materials;
            Model.TemplateId = template; Model.ApprovedMaterialId = material;
        }
        catch (ApiFailure ex)
        {
            Model.Status = ex.Message;
            if (ex.Status == 401 && !lifetime.IsCancellationRequested) new LoginWindow(api) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie udało się pobrać słowników. Wpisane dane pozostają w formularzu."; }
    }
    private void AddMaterial_Click(object sender, RoutedEventArgs e) => Model.Materials.Add(new());
    private void Firm_Click(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Button { Content: string firm }) Model.SelectInternalFirm(firm); }
    private void Category_Click(object sender, RoutedEventArgs e) { if (sender is System.Windows.Controls.Button { Tag: string category }) Model.SelectCategory(category); }
    private async void ReloadLookups_Click(object sender, RoutedEventArgs e) => await LoadLookups();
    private void RemoveMaterial_Click(object sender, RoutedEventArgs e) { if (MaterialRows.SelectedItem is OrderMaterialRow row) Model.Materials.Remove(row); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        MaterialRows.CommitEdit(); MaterialRows.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        try { if (await Model.Save(api, lifetime.Token)) DialogResult = true; }
        catch (ApiFailure ex) when (ex.Status == 401)
        {
            new LoginWindow(api) { Owner = this }.ShowDialog();
            Model.Status = "Formularz zachowano. Po zalogowaniu ponów zapis.";
        }
    }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await Model.Compare(api, lifetime.Token); }
        catch (ApiFailure ex)
        {
            Model.Status = ex.Message;
            if (ex.Status == 401 && !lifetime.IsCancellationRequested) new LoginWindow(api) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Model.Status = "Nie można pobrać danych do porównania."; }
    }
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptComparison();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
