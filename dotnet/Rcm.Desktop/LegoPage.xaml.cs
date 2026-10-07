using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class LegoPage : UserControl
{
    private CrmClient? api;
    private ILegoCartStore store = new LegoCartStore();
    private readonly Dictionary<long, LegoViewModel> models = [];
    private CancellationTokenSource request = new();
    private CancellationTokenSource rendering = new();
    private bool ready;
    public LegoViewModel Model { get; private set; } = null!;
    public bool IsWorking => models.Values.Any(model => model.IsWorking);
    public bool Dirty => models.Values.Any(model => model.Dirty);
    public LegoPage() => InitializeComponent();
    public void SetClient(CrmClient client, ILegoCartStore? cartStore = null)
    {
        api = client; if (cartStore is not null) store = cartStore; client.SessionChanged += SessionChanged; SelectModel(); ready = true;
    }
    private void SelectModel()
    {
        if (api is null) return; var id = api.Session?.UserId ?? 0;
        if (Model is not null && Model.UserId == id) { Model.Notify(); return; }
        request.Cancel(); rendering.Cancel(); if (Model is not null) Model.PropertyChanged -= ModelChanged;
        if (!models.TryGetValue(id, out var model)) { model = new(api, store, id); models[id] = model; }
        Model = model; Model.PropertyChanged += ModelChanged; DataContext = Model; LegoScene.Clear(); LegoLayer.ItemsSource = null;
        if (Model.Result is not null) RenderResult();
    }
    private async void SessionChanged(object? sender, EventArgs e) { SelectModel(); if (ready && IsVisible && api?.Session?.Role is "biuro" or "technolog") await Activate(); }
    public void Dispose() { Cancel(); if (api is not null) api.SessionChanged -= SessionChanged; if (Model is not null) Model.PropertyChanged -= ModelChanged; }
    public void Cancel() { request.Cancel(); rendering.Cancel(); }
    public void Clear()
    {
        Cancel(); if (Model is not null) Model.PropertyChanged -= ModelChanged; models.Clear(); Model = null!; SelectModel(); LegoScene.Clear();
    }
    public bool ConfirmLeave() => !Dirty || MessageBox.Show(Window.GetWindow(this), "Niezapisane wymiary lub zmiany lokalnego koszyka LEGO zostaną odrzucone. Zapisane koszyki pozostają na tym komputerze. Kontynuować?", "Dane LEGO", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private async Task Run(Func<LegoViewModel, CancellationToken, Task> action)
    {
        if (!ready || api is null) return;
        if (Model.IsWorking) return;
        request.Cancel(); request.Dispose(); request = new(); var ct = request.Token; var model = Model;
        try { await action(model, ct); }
        catch (OperationCanceledException) { if (Model == model) model.CartStatus = "Przerwano oczekiwanie. Wpisane wartości i pozycje zachowano."; }
        catch (ApiFailure ex)
        {
            if (Model != model || ct.IsCancellationRequested) return; model.Status = ex.Message;
            if (ex.Status == 401) { new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog(); Model.Status = "Po zalogowaniu oblicz ponownie. Formularz zachowano dla osoby, która go rozpoczęła."; }
        }
        catch (HttpRequestException) { if (Model == model && !ct.IsCancellationRequested) model.Status = "Brak połączenia. Wpisane wartości zachowano; ponów próbę."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { if (Model == model && !ct.IsCancellationRequested) model.CartStatus = "Nie udało się zapisać lub odczytać koszyka. Pozycje zachowano; ponów próbę."; }
    }
    public Task Activate() => Run(async (model, ct) => { await Task.WhenAll(model.LoadCatalog(ct), model.LoadCart(ct)); });
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LegoViewModel.Result)) RenderResult(); }
    private async void RenderResult()
    {
        rendering.Cancel(); rendering.Dispose(); rendering = new(); var ct = rendering.Token; var model = Model;
        var plan = model.Result; if (plan is null) { LegoScene.Clear(); LegoLayer.ItemsSource = null; return; }
        model.Rendering = true;
        try
        {
            LegoLayer.ItemsSource = Enumerable.Range(1, plan.Rows).ToArray(); LegoLayer.SelectedIndex = 0;
            var series = model.Series.Single(row => row.Key == plan.Input.Series);
            await LegoScene.SetPlan(plan, series.DepthCm, ct);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { if (Model == model) model.Status = "Nie udało się przygotować widoku. Oblicz układ ponownie."; }
        finally { model.Rendering = false; }
    }
    private async void Calculate_Click(object sender, RoutedEventArgs e) => await Run(async (model, ct) => { await model.Calculate(ct); });
    private void Stop_Click(object sender, RoutedEventArgs e) => Cancel();
    private void AddResult_Click(object sender, RoutedEventArgs e) { Model.AddResult(); if (Model.Cart.Count > 0) LegoTabs.SelectedIndex = 4; }
    private void AddProduct_Click(object sender, RoutedEventArgs e) => Model.AddProduct();
    private void Remove_Click(object sender, RoutedEventArgs e) => Model.Remove();
    private void ClearCart_Click(object sender, RoutedEventArgs e) { if (Model.CanClear && MessageBox.Show(Window.GetWindow(this), "Usunąć wszystkie pozycje lokalnego koszyka? Zapisz zmianę, aby zachować pusty koszyk.", "Wyczyść koszyk LEGO", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) Model.ClearCart(); }
    private async void SaveCart_Click(object sender, RoutedEventArgs e)
    {
        LegoCart.CommitEdit(); LegoCart.CommitEdit(DataGridEditingUnit.Row, true); await Run(async (model, ct) => { await model.SaveCart(ct); });
    }
    private async void LoadCart_Click(object sender, RoutedEventArgs e) => await Run((model, ct) => model.LoadCart(ct));
    private void ResetView_Click(object sender, RoutedEventArgs e) => LegoScene.ResetCamera();
    private void Layer_Changed(object sender, SelectionChangedEventArgs e) { if (LegoLayer.SelectedItem is int row) LegoTopPlan.Layer = row - 1; }
}
