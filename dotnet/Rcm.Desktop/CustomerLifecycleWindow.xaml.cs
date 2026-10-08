using System.ComponentModel;
using System.Net.Http;
using System.Windows;

namespace Rcm.Desktop;

public partial class CustomerLifecycleWindow : Window
{
    public CustomerLifecycleViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public CustomerLifecycleWindow(CrmClient api, CustomerLifecycleViewModel model)
    {
        this.api = api; Model = model; DataContext = model; InitializeComponent();
        Title = Heading.Text = model.Restore ? "Przywróć klienta" : "Usuń klienta";
        Confirm.Content = model.Restore ? "Przywróć klienta" : "Usuń klienta";
        Explanation.Text = model.Restore
            ? "Klient wróci na aktywną listę z zachowanymi tematami, historią i planami kontaktu. Minione terminy pojawią się jako zaległe."
            : "Klient zniknie z aktywnej listy i kolejki Do kontaktu. Jego dane, tematy i historia pozostaną w Usunięci klienci. Możesz go później przywrócić.";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Closing += GuardClose; Closed += (_, _) => lifetime.Cancel();
    }
    private void GuardClose(object? sender, CancelEventArgs e)
    {
        if (!Model.CanClose) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj na potwierdzenie."; return; }
        if (Model.IsUncertain && MessageBox.Show(this,
            "Wynik operacji jest nieznany. Ponowienie w tym oknie jest bezpieczne. Zamknąć i utracić możliwość ponowienia?",
            "Niepotwierdzona operacja", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { if (await Model.Save(api, lifetime.Token)) DialogResult = true; }
        catch (ApiFailure error) when (error.Status == 401)
        {
            new LoginWindow(api) { Owner = this }.ShowDialog();
            Model.Status = "Operacja zachowana. Po zalogowaniu potwierdź ją ponownie.";
        }
    }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await Model.ReloadComparison(api, lifetime.Token); }
        catch (ApiFailure error)
        {
            Model.Status = error.Message;
            if (error.Status == 401 && !lifetime.IsCancellationRequested) new LoginWindow(api) { Owner = this }.ShowDialog();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        { Model.Status = "Nie można pobrać danych do porównania. Spróbuj ponownie."; }
    }
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptComparedVersion();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
