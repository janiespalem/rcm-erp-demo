using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class ProductionPage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource read = new();
    private bool ready, syncing;
    public ProductionViewModel Model { get; private set; } = null!;
    public ProductionPage() { InitializeComponent(); SizeChanged += (_, _) => { ContractSummary.IsExpanded = ActualHeight >= 780; ContractReportsSummary.IsExpanded = ActualHeight >= 900; }; IsVisibleChanged += (_, _) => { if (ready && !IsVisible) CancelRead(); }; }
    public void SetClient(CrmClient client) { api = client; Model = new(client); DataContext = Model; ready = true; }
    public void CancelRead() { read.Cancel(); if (ready) Model.CancelRead(); }
    public void Clear() { CancelRead(); if (!ready) return; syncing = true; try { Model.Clear(); } finally { syncing = false; } }
    private async Task Run(Func<CancellationToken, Task> action, bool debounce = false)
    {
        if (!ready || api is null) return; read.Cancel(); read.Dispose(); read = new(); var ct = read.Token;
        try { if (debounce) await Task.Delay(300, ct); await action(ct); }
        catch (ApiFailure ex) { if (!ct.IsCancellationRequested) { Model.Status = ex.Message; if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog(); } }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Model.Status = "Brak połączenia. Spróbuj ponownie."; }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież dane."; }
        finally { if (!ct.IsCancellationRequested) Model.Notify(); }
    }
    public Task Activate(ProductionFeatures? features = null) => Run(async ct =>
    {
        if (features is null) { try { features = await api!.ReadProduction<ProductionFeatures>("/features", ct); } catch (ApiFailure ex) when (ex.Status == 404) { features = new(false, false, default); } }
        ct.ThrowIfCancellationRequested(); Model.SetFeatures(features); await Model.Reports.Initialize(ct); await Model.Load(ct);
        ct.ThrowIfCancellationRequested(); if (Model.Selected is { } selected) await Model.Open(selected.Contract.Id, ct);
    });
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct), true); } }
    private async void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (ready && !Model.Busy && Model.Selected is { } row && !syncing) await Run(ct => Model.Open(row.Contract.Id, ct)); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.CanPrevious) { Model.Page--; await Run(ct => Model.Load(ct)); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.CanNext) { Model.Page++; await Run(ct => Model.Load(ct)); } }
    private async Task Edit(bool delivery, bool create, CancellationToken ct)
    {
        var model = new ProductionEditorViewModel(Model.Features, create && !delivery ? null : Model.Detail, delivery && !create ? Model.SelectedDelivery?.Delivery : null, delivery);
        Window window = delivery ? new SteelDeliveryWindow(api!, model) : new ProductionContractWindow(api!, model); window.Owner = Window.GetWindow(this); window.ShowDialog();
        if (!model.Committed || ct.IsCancellationRequested) return; await Model.Load(ct); if (model.Contract is { } contract) { syncing = true; try { Model.Selected = Model.Rows.FirstOrDefault(r => r.Contract.Id == contract.Id); } finally { syncing = false; } await Model.Open(contract.Id, ct); } Model.Status = model.Status;
    }
    private async void Create_Click(object sender, RoutedEventArgs e) { if (Model.CanCreate) await Run(ct => Edit(false, true, ct)); }
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (Model.CanOpen) await Run(ct => Edit(false, false, ct)); }
    private async void AddDelivery_Click(object sender, RoutedEventArgs e) { if (Model.CanAddDelivery) await Run(ct => Edit(true, true, ct)); }
    private async void CorrectDelivery_Click(object sender, RoutedEventArgs e) { if (Model.CanCorrectDelivery) await Run(ct => Edit(true, false, ct)); }
    private async void PreviousDelivery_Click(object sender, RoutedEventArgs e) { if (Model.CanPreviousDelivery) { Model.DeliveryPage--; await Run(ct => Model.LoadDeliveries(ct)); } }
    private async void NextDelivery_Click(object sender, RoutedEventArgs e) { if (Model.CanNextDelivery) { Model.DeliveryPage++; await Run(ct => Model.LoadDeliveries(ct)); } }
    private async void Reports_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Reports.Load(ct));
    private async void Queue_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Reports.LoadQueue(ct));
    private async void ReportsPrevious_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanPrevious) { Model.Reports.Page--; await Run(ct => Model.Reports.Load(ct)); } }
    private async void ReportsNext_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanNext) { Model.Reports.Page++; await Run(ct => Model.Reports.Load(ct)); } }
    private async void QueuePrevious_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanPreviousQueue) { Model.Reports.QueuePage--; await Run(ct => Model.Reports.LoadQueue(ct)); } }
    private async void QueueNext_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanNextQueue) { Model.Reports.QueuePage++; await Run(ct => Model.Reports.LoadQueue(ct)); } }
    private async Task OpenReport(ProductionReportRow? row, CancellationToken ct)
    {
        if (row is null) return;
        var features = await api!.ReadShiftReports<Rcm.Contracts.ShiftReportFeatures>("/features", ct);
        var state = await api.ReadProduction<ProductionReportReviewState>($"/reports/{row.Value.Report.Id}", ct); ct.ThrowIfCancellationRequested();
        var model = new ShiftReportsEditorViewModel(state.Report, features); var window = new ShiftReportsEditorWindow(api, model) { Owner = Window.GetWindow(this) };
        window.ShowDialog(); if (!model.Committed || ct.IsCancellationRequested) return;
        if (Model.Reports.ContractId is not null) await Model.Reports.Load(ct);
        if (Model.Reports.CanReview) await Model.Reports.LoadQueue(ct);
    }
    private async void OpenContractReport_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanOpenReport) await Run(ct => OpenReport(Model.Reports.SelectedReport, ct)); }
    private async void OpenQueueReport_Click(object sender, RoutedEventArgs e) { if (Model.Reports.CanOpenQueue) await Run(ct => OpenReport(Model.Reports.SelectedQueue, ct)); }
    private async void Audit_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.LoadAudit(ct));
    private async void PreviousAudit_Click(object sender, RoutedEventArgs e) { if (Model.CanPreviousAudit) { Model.AuditPage--; await Run(ct => Model.LoadAudit(ct)); } }
    private async void NextAudit_Click(object sender, RoutedEventArgs e) { if (Model.CanNextAudit) { Model.AuditPage++; await Run(ct => Model.LoadAudit(ct)); } }
}
