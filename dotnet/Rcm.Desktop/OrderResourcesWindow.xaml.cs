using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderResourcesWindow : Window
{
    public OrderResourcesViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    private bool syncing;

    public OrderResourcesWindow(CrmClient api, OrderDto order, long? initialQuestionId = null)
    {
        this.api = api; Model = new(order); Model.RefreshAccess(api);
        InitializeComponent(); DataContext = Model;
        Heading.Text = $"Operacje i pytania · {order.OrderNumber ?? $"#{order.Id}"}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += async (_, _) => await Run(async () =>
        {
            await Model.Load(api, lifetime.Token);
            if (initialQuestionId is { } id && Model.Questions.SingleOrDefault(q => q.Id == id) is { } question)
                Model.SelectQuestion(question);
        });
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj na odpowiedź."; }
            else if (Model.Dirty && MessageBox.Show(this, Model.Uncertain
                ? "Wynik zapisu jest nieznany. Po zamknięciu sprawdź dane zlecenia przed ponownym wpisaniem tej operacji. Zamknąć formularz?"
                : "Zamknąć formularz i odrzucić niezapisane dane?", "Niezapisane dane", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }

    private void SessionChanged(object? sender, EventArgs e) => Model.RefreshAccess(api);
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (ApiFailure ex)
        {
            Model.Status = ex.Message + " Dane formularza zachowano.";
            if (ex.Status == 401 && !lifetime.IsCancellationRequested)
            {
                new LoginWindow(api) { Owner = this }.ShowDialog(); Model.RefreshAccess(api);
                Model.Status = Model.Uncertain ? "Treść zachowano. Ponów ten sam zapis po zalogowaniu." : "Treść zachowano. Odśwież dane po zalogowaniu.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie udało się wczytać danych. Treść zachowano; możesz spróbować ponownie."; }
        finally { if (!lifetime.IsCancellationRequested) SyncSelection(); }
    }
    private bool LeaveDraft()
    {
        if (Model.Busy || Model.Uncertain) return false;
        if (!Model.Dirty) return true;
        if (MessageBox.Show(this, "Odrzucić niezapisane dane przed zmianą widoku?", "Niezapisane dane",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        Model.DiscardDraft(); return true;
    }
    private void SyncSelection()
    {
        syncing = true;
        try { ResourcesOperations.SelectedItem = Model.SelectedOperation; ResourcesQuestions.SelectedItem = Model.SelectedQuestion; }
        finally { syncing = false; }
    }
    private void Operation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || Model.Busy) return;
        if (ResourcesOperations.SelectedItem is OrderOperationRow row && row != Model.SelectedOperation && LeaveDraft()) Model.SelectOperation(row);
        SyncSelection();
    }
    private void Question_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || Model.Busy) return;
        if (ResourcesQuestions.SelectedItem is OrderQuestionDto row && row != Model.SelectedQuestion && LeaveDraft()) Model.SelectQuestion(row);
        SyncSelection();
    }
    private void NewQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanAsk || !LeaveDraft()) return;
        Model.BeginQuestion(); SyncSelection(); ResourceDraft.Focus();
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { if (Model.CanReload && LeaveDraft()) await Run(() => Model.Load(api, lifetime.Token)); }
    private async void Save_Click(object sender, RoutedEventArgs e) => await Run(async () => { await Model.Save(api, lifetime.Token); });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
