using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class CatalogPage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private bool ready;
    private bool clearing;
    public CatalogViewModel Model { get; private set; } = null!;
    public CatalogPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (ready && !IsVisible) CancelRead(); };
    }
    public void SetClient(CrmClient client, CatalogKind kind)
    {
        api = client; Model = new(client, kind); DataContext = Model; ready = true;
        GroupColumn.Header = Model.GroupLabel; RateColumn.Header = Model.RateLabel;
        StateColumn.Visibility = Model.IsMaterials ? Visibility.Visible : Visibility.Collapsed;
    }
    public void CancelRead() { request.Cancel(); if (ready) Model.CancelRead(); }
    public void Clear()
    {
        CancelRead(); clearing = true;
        try { Model.Clear(); } finally { clearing = false; }
    }
    private async Task Run(Func<CancellationToken, Task> action, bool debounce = false)
    {
        if (!ready || api is null) return;
        request.Cancel(); request.Dispose(); request = new();
        var ct = request.Token;
        try { if (debounce) await Task.Delay(300, ct); await action(ct); }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież katalog."; }
        catch (ApiFailure ex)
        {
            if (ct.IsCancellationRequested) return;
            Model.Failed = true; Model.Status = ex.Message;
            if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) { Model.Failed = true; Model.Status = "Brak połączenia. Spróbuj ponownie."; } }
        finally { if (!ct.IsCancellationRequested) Model.NotifyActions(); }
    }
    public Task Activate() => Run(async ct =>
    {
        try
        {
            var features = await api!.ReadCatalog<CatalogFeatures>("/features", ct);
            ct.ThrowIfCancellationRequested();
            Model.Available = Model.IsMaterials ? features.Materials : features.Operations;
            Model.WriteEnabled = features.Write; Model.LaborRate = features.LaborRatePln;
        }
        catch (ApiFailure ex) when (ex.Status == 404)
        { ct.ThrowIfCancellationRequested(); Model.Available = Model.WriteEnabled = false; Model.Status = "Ten serwer nie obsługuje jeszcze katalogu natywnego."; }
        await Model.Load(ct);
    });
    private async void Search_Changed(object sender, TextChangedEventArgs e)
    { if (ready && !clearing) { Model.Page = 1; await Run(ct => Model.Load(ct), true); } }
    private async void Archive_Changed(object sender, RoutedEventArgs e)
    { if (ready && !clearing) { Model.Page = 1; await Run(ct => Model.Load(ct)); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private async void Previous_Click(object sender, RoutedEventArgs e)
    { if (Model.CanPrevious) { Model.Page--; await Run(ct => Model.Load(ct)); } }
    private async void Next_Click(object sender, RoutedEventArgs e)
    { if (Model.CanNext) { Model.Page++; await Run(ct => Model.Load(ct)); } }
    private async Task Edit(CatalogRow? row, bool remove = false)
    {
        if (api is null || !Model.CanWrite || remove && !Model.CanRemove) return;
        CancelRead();
        var editor = new CatalogEditorViewModel(Model.Kind, row, remove, Model.LaborRate) { WriteEnabled = Model.WriteEnabled };
        editor.RefreshAccess(api);
        if (new CatalogEditorWindow(api, editor) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
        {
            await Run(ct => Model.Load(ct));
            Model.Status = editor.Status;
        }
        Model.NotifyActions();
    }
    private async void Create_Click(object sender, RoutedEventArgs e) => await Edit(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) await Edit(Model.Selected); }
    private async void Remove_Click(object sender, RoutedEventArgs e) { if (Model.CanRemove) await Edit(Model.Selected, true); }
    private void Rows_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Edit_Click(sender, e); } }
}
