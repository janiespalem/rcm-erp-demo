using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class ShiftReportsPage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private bool ready;
    private bool syncing;
    public ShiftReportsViewModel Model { get; private set; } = null!;
    public ShiftReportsPage() { InitializeComponent(); SizeChanged += (_, _) => { ReportFilters.IsExpanded = ActualHeight >= 620; DailySummary.IsExpanded = ActualHeight >= 700; }; IsVisibleChanged += (_, _) => { if (ready && !IsVisible) CancelRead(); }; }
    public void SetClient(CrmClient client) { api = client; Model = new(client); DataContext = Model; ready = true; }
    public void CancelRead() { request.Cancel(); if (ready) Model.CancelRead(); }
    public void Clear() { CancelRead(); syncing = true; try { Model.Clear(); } finally { syncing = false; } }
    private async Task Run(Func<CancellationToken, Task> action, bool debounce = false)
    {
        if (!ready || api is null) return;
        request.Cancel(); request.Dispose(); request = new(); var ct = request.Token;
        try { if (debounce) await Task.Delay(300, ct); await action(ct); }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież dane."; }
        catch (ApiFailure ex) { if (!ct.IsCancellationRequested) { Model.Status = ex.Message; if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog(); } }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Model.Status = "Brak połączenia. Spróbuj ponownie."; }
        finally { if (!ct.IsCancellationRequested) Model.Notify(); }
    }
    public Task Activate(ShiftReportFeatures? features = null) => Run(async ct =>
    {
        if (features is null)
        {
            try { features = await api!.ReadShiftReports<ShiftReportFeatures>("/features", ct); }
            catch (ApiFailure ex) when (ex.Status == 404) { features = new(false, false, false, new Dictionary<int, IReadOnlyList<ShiftReportQuestion>>()); }
        }
        ct.ThrowIfCancellationRequested(); Model.SetFeatures(features); await Model.Load(ct);
    });
    private async Task Edit(ShiftReportDto? report, CancellationToken ct)
    {
        if (api is null) return;
        var model = new ShiftReportsEditorViewModel(report, Model.Features, Model.EntryDate, Model.EntryShift);
        new ShiftReportsEditorWindow(api, model) { Owner = Window.GetWindow(this) }.ShowDialog();
        if (model.Committed && !ct.IsCancellationRequested) { await Model.Load(ct); Model.Status = model.Status; }
    }
    private async Task Open(long id, CancellationToken ct) { if (await Model.Open(id, ct) is { } report) await Edit(report, ct); }
    private async void Open_Click(object sender, RoutedEventArgs e) { if (Model.CanOpen && Model.Selected is { } row) await Run(ct => Open(row.Report.Id, ct)); }
    private async void Today_Click(object sender, RoutedEventArgs e) { if (!Model.Busy && sender is Button { Tag: long id }) await Run(ct => Open(id, ct)); }
    private async void OpenDate_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || Model.Busy || !Model.Features.Read) return;
        if (Model.EntryDate is not { } date) { Model.Status = "Wybierz datę raportu."; return; }
        await Run(async ct =>
        {
            var day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var rows = await api.ReadShiftReports<Page<ShiftReportDto>>($"?dateFrom={day}&dateTo={day}&shift={Uri.EscapeDataString(Model.EntryShift)}&page=1&pageSize=50", ct); ct.ThrowIfCancellationRequested();
            if (rows.Items.FirstOrDefault() is { } report) await Open(report.Id, ct);
            else if (Model.CanCreate) await Edit(null, ct); else Model.Status = "Nie ma raportu dla tej daty i zmiany.";
        });
    }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct), true); } }
    private void Shift_Changed(object sender, SelectionChangedEventArgs e) => Filter_Changed(sender, e);
    private async void Deleted_Changed(object sender, RoutedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct)); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.CanPrevious) { Model.Page--; await Run(ct => Model.Load(ct)); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.CanNext) { Model.Page++; await Run(ct => Model.Load(ct)); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private void Rows_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
}
