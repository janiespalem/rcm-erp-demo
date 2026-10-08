using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderQuoteWindow : Window
{
    public OrderQuoteViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public OrderQuoteWindow(CrmClient api, OrderDto order)
    {
        this.api = api; Model = new(order); InitializeComponent();
        Heading.Text = $"Wycena · {order.OrderNumber}";
        var methods = new Dictionary<string, string> { ["kalkulacja"] = "Kalkulacja kosztów", ["reczna"] = "Cena ręczna" };
        if (!order.IsInternal) methods.Add("od_masy", "Cena od masy");
        Methods.ItemsSource = methods; WeightBases.ItemsSource = new Dictionary<string, string> { ["netto"] = "Netto", ["brutto"] = "Brutto" };
        DataContext = Model; Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += async (_, _) => await Load();
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj na odpowiedź."; }
            else if (!Model.Saved && Model.Dirty && MessageBox.Show(this, "Zamknąć formularz i odrzucić niezapisane wartości? Wynik niepotwierdzonego zapisu sprawdź w zleceniu.",
                "Niezapisana wycena", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => lifetime.Cancel();
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (ApiFailure ex)
        {
            Model.Status = ex.Message;
            if (ex.Status == 401 && !lifetime.IsCancellationRequested) new LoginWindow(api) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) Model.Status = "Brak potwierdzonej odpowiedzi. Dane formularza zachowano."; }
    }
    private Task Load() => Run(async () =>
    {
        await Model.Load(api, lifetime.Token);
        var lookups = await api.ReadOrders<OrderLookups>("/lookups", lifetime.Token);
        OperationCatalog.ItemsSource = lookups.Operations; MaterialCatalog.ItemsSource = lookups.Materials;
    });
    private async void Reload_Click(object sender, RoutedEventArgs e) { if (Model.CanLoad) await Load(); }
    private void CommitRows() { foreach (var grid in new[] { Processes, Materials }) { grid.CommitEdit(); grid.CommitEdit(DataGridEditingUnit.Row, true); } }
    private async void Preview_Click(object sender, RoutedEventArgs e) { CommitRows(); await Run(() => Model.Preview(api, lifetime.Token)); }
    private async void Save_Click(object sender, RoutedEventArgs e) { CommitRows(); await Run(async () => { if (await Model.Save(api, lifetime.Token)) DialogResult = true; }); }
    private async void Verify_Click(object sender, RoutedEventArgs e) => await Run(() => Model.Verify(api, lifetime.Token));
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptVerified();
    private void AddProcess_Click(object sender, RoutedEventArgs e)
    {
        var operation = OperationCatalog.SelectedItem as OrderOperationDto;
        Model.Processes.Add(new() { Name = operation?.Name ?? "", Department = operation?.Department ?? "", Rate = (operation?.DefaultRate ?? 0).ToString() });
    }
    private void RemoveProcess_Click(object sender, RoutedEventArgs e) { if (Processes.SelectedItem is QuoteProcessRow row) Model.Processes.Remove(row); }
    private void AddMaterial_Click(object sender, RoutedEventArgs e)
    {
        var material = MaterialCatalog.SelectedItem as OrderApprovedMaterialDto;
        Model.Materials.Add(new() { Name = material?.Name ?? "", Price = (material?.DefaultRatePlnKg ?? 0).ToString() });
    }
    private void RemoveMaterial_Click(object sender, RoutedEventArgs e) { if (Materials.SelectedItem is QuoteMaterialRow row) Model.Materials.Remove(row); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
