using System.Windows;
using System.Windows.Controls;

namespace Rcm.Desktop;

public partial class TetrapodPage : UserControl
{
    private CrmClient? api;
    private ITetrapodDeliveryStore store = new TetrapodDeliveryStore();
    private readonly Dictionary<long, TetrapodViewModel> models = [];
    private CancellationTokenSource? calculation;
    private bool attachedOwner;
    public TetrapodViewModel Model { get; private set; } = new();
    public TetrapodPage()
    {
        InitializeComponent(); DataContext = Model;
        Loaded += async (_, _) =>
        {
            if (!attachedOwner && Window.GetWindow(this) is { } owner)
            {
                attachedOwner = true;
                owner.Closed += (_, _) => { CancelCalculation(); if (api is not null) api.SessionChanged -= SessionChanged; };
            }
            await LoadHistory();
        };
    }
    public void SetClient(CrmClient client, ITetrapodDeliveryStore? deliveryStore = null)
    {
        if (api is not null) api.SessionChanged -= SessionChanged;
        CancelCalculation(); api = client; if (deliveryStore is not null) { store = deliveryStore; models.Clear(); }
        api.SessionChanged += SessionChanged; SelectModel();
    }
    private void SelectModel()
    {
        if (api is null) return;
        var id = api.Session?.UserId ?? 0;
        foreach (var existing in models.Values) existing.DeliveryAccess = false;
        if (Model.UserId != id) CancelCalculation();
        if (!models.TryGetValue(id, out var model)) { model = new(id, store); models[id] = model; }
        model.AdditionalDirty = () => models.Values.Any(other => other != model && other.LocalDirty);
        model.DeliveryAccess = api.Session?.Role is "biuro" or "technolog";
        Model = model; DataContext = Model;
    }
    private async void SessionChanged(object? sender, EventArgs e) { SelectModel(); await LoadHistory(); }
    public async void FocusInput() { Planned.Focus(); await LoadHistory(); }
    public void CancelCalculation() => calculation?.Cancel();
    public void Clear()
    {
        CancelCalculation(); calculation?.Dispose(); calculation = null; models.Clear(); Model = new(); SelectModel(); DataContext = Model;
    }
    private async Task LoadHistory()
    {
        if (api is null || !Model.CanReadDeliveries) return;
        await Run((model, ct) => model.LoadDeliveries(ct));
    }
    private async Task Run(Func<TetrapodViewModel, CancellationToken, Task<bool>> action)
    {
        if (api is null || Model.IsCalculating) return;
        calculation?.Dispose(); calculation = new(); var model = Model;
        await action(model, calculation.Token);
    }
    private async void Calculate_Click(object sender, RoutedEventArgs e)
    {
        if (api?.Session?.Role is not ("biuro" or "technolog")) return;
        try { await Run((model, ct) => model.Calculate(api, ct)); }
        catch (ApiFailure ex) when (ex.Status == 401)
        {
            new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog();
            Model.Status = "Po zalogowaniu wybierz Oblicz. Formularz zachowano dla konta, które go rozpoczęło.";
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => CancelCalculation();
    private void AddDelivery_Click(object sender, RoutedEventArgs e) => Model.AddDelivery();
    private void RemoveDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (Model.CanManageDeliveries && (sender as FrameworkElement)?.DataContext is TetrapodDeliveryRow row &&
            MessageBox.Show(Window.GetWindow(this), "Usunąć tę dostawę z lokalnej historii? Zapisz historię, aby zachować zmianę.", "Usuń dostawę", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            Model.RemoveDelivery(row.Entry.Id);
    }
    private async void SaveHistory_Click(object sender, RoutedEventArgs e) => await Run((model, ct) => model.SaveDeliveries(ct));
    private async void LoadHistory_Click(object sender, RoutedEventArgs e) => await LoadHistory();
}
