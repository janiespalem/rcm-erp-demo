using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderQuestionQueuePage : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private bool ready;
    public OrderQuestionQueueViewModel Model { get; private set; } = null!;
    public OrderQuestionQueuePage()
    {
        InitializeComponent();
        QuestionStatus.ItemsSource = new Dictionary<string, string> { [""] = "Wszystkie pytania", ["pending"] = "Czekają na odpowiedź", ["answered"] = "Odpowiedziano" };
    }
    public void SetClient(CrmClient client) { api = client; Model = new(client); DataContext = Model; ready = true; }
    public void CancelRead() { request.Cancel(); Model.CancelRead(); }
    public void Clear() { request.Cancel(); Model.Clear(); }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (!ready || api is null) return;
        request.Cancel(); request.Dispose(); request = new();
        var ct = request.Token;
        try { await action(ct); }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież dane."; }
        catch (ApiFailure ex)
        {
            if (ct.IsCancellationRequested) return;
            Model.Status = ex.Message;
            if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Model.Status = "Brak połączenia. Spróbuj ponownie."; }
    }
    public Task Activate() => Run(async ct =>
    {
        try { var features = await api!.ReadOrders<OrderFeatures>("/features", ct); ct.ThrowIfCancellationRequested(); Model.Available = features.Resources; }
        catch (ApiFailure ex) when (ex.Status == 404) { ct.ThrowIfCancellationRequested(); Model.Available = false; }
        ct.ThrowIfCancellationRequested();
        if (!Model.Available) { Model.Rows.Clear(); Model.Selected = null; Model.Status = "Kolejka pytań będzie dostępna po przełączeniu modułu zleceń."; return; }
        Model.NotifyActions(); await Model.Load(ct);
    });
    private Task Reload(bool debounce = false) => Run(async ct => { if (debounce) await Task.Delay(300, ct); await Model.Load(ct); });
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ready) { Model.Page = 1; await Reload(true); } }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (ready) { Model.Page = 1; await Reload(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.CanPrevious) { Model.Page--; await Reload(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.CanNext) { Model.Page++; await Reload(); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanOpen || api is null || Model.Selected is not { } row) return;
        await Run(async ct =>
        {
            var order = await Model.OpenSelected(ct);
            ct.ThrowIfCancellationRequested();
            if (order is not null) new OrderResourcesWindow(api, order, row.Question.Id) { Owner = Window.GetWindow(this) }.ShowDialog();
        });
        if (IsVisible) await Reload();
    }
    private void Questions_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
}
