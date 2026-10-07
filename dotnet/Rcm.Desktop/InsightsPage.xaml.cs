using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Rcm.Desktop;

public partial class InsightsPage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private CancellationTokenSource? export;
    private bool ready;
    private bool suppressEvents;
    private (long? User, string? Role) identity;
    public InsightsViewModel Model { get; private set; } = null!;
    public InsightsPage() { InitializeComponent(); SizeChanged += (_, _) => RevenueDetails.IsExpanded = ActualHeight >= 700; Unloaded += (_, _) => CancelRead(); }
    public void SetClient(CrmClient client)
    {
        api = client; Model = new(client); DataContext = Model;
        identity = (client.Session?.UserId, client.Session?.Role);
        Model.Columns.CollectionChanged += (_, _) => RebuildColumns();
        client.SessionChanged += SessionChanged;
        ready = true;
    }
    private void SessionChanged(object? sender, EventArgs e)
    {
        var current = (api?.Session?.UserId, api?.Session?.Role);
        if (identity == current) return;
        identity = current;
        Dispatcher.Invoke(Clear);
    }
    public void Dispose() { CancelRead(); if (api is not null) api.SessionChanged -= SessionChanged; }
    public void CancelRead() { suppressEvents = false; request.Cancel(); export?.Cancel(); Model.CancelRead(); }
    public void Clear() { ready = false; CancelRead(); Model.Clear(); ready = true; }
    private void RebuildColumns()
    {
        InsightList.Columns.Clear();
        foreach (var c in Model.Columns) InsightList.Columns.Add(new DataGridTextColumn { Header = c.Title, Binding = new Binding($"[{c.Key}]"), Width = c.Width });
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (!ready || api is null) return;
        request.Cancel(); request.Dispose(); request = new(); var ct = request.Token;
        try { await action(ct); }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież raport."; }
        catch (ApiFailure ex)
        {
            if (ct.IsCancellationRequested) return;
            Model.Status = ex.Message;
            if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Model.Status = "Brak połączenia. Kryteria zachowano; odśwież raport."; }
    }
    public Task Activate() => Run(async ct =>
    {
        suppressEvents = true;
        try { await Model.Activate(ct); }
        finally { if (!ct.IsCancellationRequested) suppressEvents = false; }
    });
    private Task Reload(bool delay = false) => Run(async ct => { if (delay) await Task.Delay(300, ct); await Model.Load(ct); });
    private async void Report_Changed(object sender, SelectionChangedEventArgs e) { if (ready && !suppressEvents && IsVisible && ReportSelector.SelectedItem is InsightChoice choice) { Model.Section = choice.Section; await Reload(); } }
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ready && !suppressEvents && IsVisible) { Model.Page = 1; await Reload(true); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.CanPrevious) { Model.Page--; await Reload(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.CanNext) { Model.Page++; await Reload(); } }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || !Model.CanExport) return;
        var dialog = new SaveFileDialog { FileName = "zlecenia_rcm.xlsx", Filter = "Arkusz Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await ExportTo(dialog.FileName);
    }
    public async Task ExportTo(string destination)
    {
        if (api is null || !Model.CanExport) return;
        export = new(); var ct = export.Token; Model.Exporting = true; Model.Status = "Zapisywanie arkusza…";
        try { await api.DownloadInsightsXlsx(destination, ct); Model.Status = "Arkusz zapisano: " + Path.GetFileName(destination); }
        catch (OperationCanceledException) { Model.Status = ct.IsCancellationRequested ? "Zapis anulowano." : "Upłynął czas oczekiwania. Ponów zapis."; }
        catch (ApiFailure ex) { Model.Status = ex.Message; if (ex.Status == 401 && !ct.IsCancellationRequested) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog(); }
        catch (HttpRequestException) { Model.Status = "Brak połączenia. Ponów zapis arkusza."; }
        catch (IOException) { Model.Status = "Nie można zapisać pliku. Sprawdź folder i czy arkusz jest zamknięty."; }
        catch (UnauthorizedAccessException) { Model.Status = "Brak uprawnień do wybranego folderu."; }
        finally { export.Dispose(); export = null; Model.Exporting = false; }
    }
    public void CancelExport() => export?.Cancel();
    private void CancelExport_Click(object sender, RoutedEventArgs e) => CancelExport();
}
