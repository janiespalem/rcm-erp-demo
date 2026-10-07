using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderDocumentsWindow : Window
{
    public OrderDocumentsViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    public OrderDocumentsWindow(CrmClient api, OrderDto order)
    {
        this.api = api; Model = new(order) { Role = api.Session?.Role };
        InitializeComponent(); DataContext = Model;
        Heading.Text = $"Dokumenty · {order.OrderNumber ?? $"#{order.Id}"}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        api.SessionChanged += SessionChanged;
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.Role = api.Session?.Role;
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } || operation is not null || !Model.CanDownloadKind(kind)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Zapisz dokument", FileName = Model.Filename(kind),
            Filter = kind == "operations" ? "Archiwum ZIP (*.zip)|*.zip" : "Dokument PDF (*.pdf)|*.pdf|Dokument HTML (*.html)|*.html",
            DefaultExt = kind == "operations" ? ".zip" : ".pdf", AddExtension = true,
            OverwritePrompt = true, CheckPathExists = true, ValidateNames = true
        };
        if (dialog.ShowDialog(this) != true) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = request;
        try { await Model.Download(api, kind, dialog.FileName, request.Token); }
        catch (ApiFailure ex)
        {
            if (lifetime.IsCancellationRequested) return;
            Model.Status = ex.Message;
            if (ex.Status == 401)
            {
                new LoginWindow(api) { Owner = this }.ShowDialog();
                Model.Role = api.Session?.Role;
                Model.Status = "Po zalogowaniu ponów pobieranie dokumentu.";
            }
        }
        catch (OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Pobieranie anulowane lub upłynął czas oczekiwania. Możesz spróbować ponownie."; }
        catch (HttpRequestException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Brak połączenia. Ponów pobieranie dokumentu."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie udało się zapisać dokumentu. Sprawdź uprawnienia i wolne miejsce albo wybierz inny folder."; }
        finally { operation = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
