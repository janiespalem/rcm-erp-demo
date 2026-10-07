using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderAttachmentsWindow : Window
{
    public OrderAttachmentsViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;

    public OrderAttachmentsWindow(CrmClient api, OrderDto order)
    {
        this.api = api; Model = new(order) { Role = api.Session?.Role };
        InitializeComponent(); DataContext = Model;
        Heading.Text = $"Załączniki · {order.OrderNumber ?? $"#{order.Id}"}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += async (_, _) => await Run(ct => Model.Load(api, ct));
        Closed += (_, _) => lifetime.Cancel();
        Closing += (_, e) =>
        {
            if ((Model.Busy || Model.HasPendingUpload) && MessageBox.Show(this,
                Model.Busy ? "Wynik operacji może być nieznany. Zamknąć okno?" : "Wybrany plik nie został dodany. Zamknąć okno?",
                "Załączniki", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || lifetime.IsCancellationRequested) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = request;
        try { await action(request.Token); }
        catch (ApiFailure ex)
        {
            if (lifetime.IsCancellationRequested) return;
            Model.Status = ex.Message;
            if (ex.Status == 401)
            {
                new LoginWindow(api) { Owner = this }.ShowDialog();
                Model.Role = api.Session?.Role;
                Model.Status = "Po zalogowaniu ponów operację. Wybrany plik i załącznik zachowano.";
            }
        }
        catch (OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie potwierdzono operacji. Wybrany plik zachowano; ponów z tym samym plikiem lub odśwież listę."; }
        catch (HttpRequestException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Brak połączenia. Wybrany plik zachowano; ponów operację lub odśwież listę."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie udało się zapisać pliku. Sprawdź uprawnienia i wolne miejsce albo wybierz inny folder."; }
        finally { operation = null; }
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Load(api, ct));
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanChooseFile) return;
        var dialog = new OpenFileDialog { Title = "Dodaj załącznik", CheckFileExists = true, Multiselect = false,
            Filter = "Załączniki (*.pdf;*.dxf;*.dwg;*.jpg;*.jpeg;*.png;*.xlsx;*.docx)|*.pdf;*.dxf;*.dwg;*.jpg;*.jpeg;*.png;*.xlsx;*.docx" };
        if (dialog.ShowDialog(this) == true) Model.UploadPath = dialog.FileName;
    }
    private async void Upload_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Upload(api, ct));
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanRemove || Model.Selected is not { } item) return;
        if (MessageBox.Show(this, $"Usunąć załącznik „{item.Filename}”?", "Usuń załącznik",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await Run(ct => Model.Remove(api, ct));
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanDownload || Model.Selected is not { } item || operation is not null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Zapisz załącznik", FileName = OrderAttachmentsViewModel.SafeFilename(item),
            Filter = "Wszystkie pliki (*.*)|*.*", AddExtension = false, OverwritePrompt = true,
            CheckPathExists = true, ValidateNames = true
        };
        if (dialog.ShowDialog(this) == true) await Run(ct => Model.Download(api, dialog.FileName, ct));
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
