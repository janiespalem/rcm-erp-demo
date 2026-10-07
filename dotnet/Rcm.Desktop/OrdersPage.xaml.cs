using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrdersPage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private bool ready;
    private bool active;
    private bool mutation;
    public OrdersViewModel Model { get; private set; } = null!;
    public OrdersPage() { InitializeComponent(); StatusFilter.ItemsSource = OrderLabels.Statuses; }
    public void SetClient(CrmClient client) { api = client; Model = new(client); DataContext = Model; ready = true; }
    public async Task Activate()
    {
        if (!ready) return;
        active = true; Model.NotifyActions();
        await Run(async ct =>
        {
            try
            {
                var features = await api!.ReadOrders<OrderFeatures>("/features", ct);
                ct.ThrowIfCancellationRequested(); Model.SupportsResources = features.Resources; Model.SupportsAttachments = features.Attachments;
                Model.SupportsDocuments = features.Documents; Model.SupportsTemplates = features.Templates;
                Model.SupportsIntake = features.Intake;
            }
            catch (ApiFailure ex) when (ex.Status == 404) { ct.ThrowIfCancellationRequested(); Model.SupportsResources = Model.SupportsAttachments = Model.SupportsDocuments = Model.SupportsTemplates = Model.SupportsIntake = false; }
            if (!Model.DetailVisible) await Model.Load(ct);
        });
    }
    public void CancelRead() { active = false; if (!mutation) request.Cancel(); }
    public void Clear() { active = false; request.Cancel(); ready = false; try { Model.Clear(); } finally { ready = true; } }
    public bool IsSaving => mutation;
    private CancellationToken Begin() { request.Cancel(); request.Dispose(); request = new(); return request.Token; }
    private async Task Run(Func<CancellationToken, Task> operation, bool write = false)
    {
        if (api is null || mutation) return;
        var ct = Begin(); mutation = write;
        if (write) { Model.Busy = true; Model.Status = "Zapisywanie…"; }
        try { await operation(ct); }
        catch (OperationCanceledException) { if (write) Model.NeedsRefresh = true; if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież dane."; }
        catch (ApiFailure ex)
        {
            if (ct.IsCancellationRequested) return;
            Model.Status = ex.Message;
            if (write && ex.Status >= 500) Model.NeedsRefresh = true;
            if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (HttpRequestException) { if (write) Model.NeedsRefresh = true; if (!ct.IsCancellationRequested) Model.Status = write ? "Wynik operacji jest nieznany. Odśwież zlecenie przed kolejną operacją." : "Brak połączenia. Spróbuj ponownie."; }
        finally { if (write) { mutation = false; Model.Busy = false; } Model.NotifyActions(); }
    }
    private Task Reload(bool debounce = false) => Run(async ct => { if (debounce) await Task.Delay(300, ct); await Model.Load(ct); });
    private Task Open(long id) => Run(ct => Model.Open(id, ct));
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ready && active && !mutation) { Model.Page = 1; await Reload(true); } }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (ready && active && !mutation) { Model.Page = 1; await Reload(); } }
    private async void Archive_Changed(object sender, RoutedEventArgs e) { if (ready && active && !mutation) { Model.Page = 1; await Reload(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.Page > 1) { Model.Page--; await Reload(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.Page * 50 < Model.Total) { Model.Page++; await Reload(); } }
    private async void Open_Click(object sender, RoutedEventArgs e) { if (Orders.SelectedItem is OrderListRow row) await Open(row.Order.Id); }
    private void Orders_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
    private async void Back_Click(object sender, RoutedEventArgs e) { if (!mutation) { Model.DetailVisible = false; await Reload(); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (Model.DetailVisible && Model.Detail is { } order) await Open(order.Id); else await Reload(); }
    private async Task Edit(OrderDto? order)
    {
        if (api is null || mutation || !Model.CanCreate) return;
        request.Cancel();
        var editor = new OrderEditorViewModel(order, intake: order is null && Model.SupportsIntake);
        if (new OrderEditorWindow(api, editor) { Owner = Window.GetWindow(this) }.ShowDialog() == true && editor.Saved is { } saved)
        {
            Model.AcceptSaved(saved);
            await Open(saved.Id);
            Model.Status = $"Zapisano zlecenie {saved.OrderNumber ?? $"#{saved.Id}"}. {editor.Status}";
        }
    }
    private async void Create_Click(object sender, RoutedEventArgs e) => await Edit(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) await Edit(Model.Detail); }
    private async void Triage_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || !Model.CanTriage || Model.Detail is not { } order) return;
        await Run(async ct =>
        {
            var result = await api.WriteOrder<OrderTriageResult>(HttpMethod.Post, $"/{order.Id}/triage", new { }, ct);
            await Model.Open(order.Id, ct);
            Model.Status = string.Join("\n", new[] { result.Message }.Concat(result.Warnings));
        }, true);
    }
    private async void Transition_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || Model.Detail is not { } order || sender is not Button { Tag: string action } button) return;
        if (!Model.CanTransition(action)) return;
        if (MessageBox.Show(Window.GetWindow(this), $"{button.Content}: {order.OrderNumber}?", "Potwierdź operację", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct =>
        {
            await api.WriteOrder<OrderDto>(HttpMethod.Post, $"/{order.Id}/{action}", new { }, ct);
            await Model.Open(order.Id, ct);
        }, true);
    }
    private void Resources_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || !Model.CanResources || Model.Detail is not { } order) return;
        request.Cancel();
        new OrderResourcesWindow(api, order) { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh_Click(sender, e);
    }
    private void Attachments_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || !Model.CanAttachments || Model.Detail is not { } order) return;
        request.Cancel();
        new OrderAttachmentsWindow(api, order) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void Quote_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || !Model.CanQuote || Model.Detail is not { } order) return;
        request.Cancel();
        new OrderQuoteWindow(api, order) { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh_Click(sender, e);
    }
    private void Documents_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || !Model.CanDocuments || Model.Detail is not { } order) return;
        request.Cancel();
        new OrderDocumentsWindow(api, order) { Owner = Window.GetWindow(this) }.ShowDialog();
        Model.NotifyActions();
    }
    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || mutation || !Model.CanSaveTemplate || Model.Detail is not { } order) return;
        request.Cancel();
        var editor = new OrderTemplateWindow(api, order) { Owner = Window.GetWindow(this) };
        editor.ShowDialog();
        if (editor.Model.Saved is { } saved) Model.Status = $"Zapisano szablon: {saved.Name}.";
        Model.NotifyActions();
    }
}
