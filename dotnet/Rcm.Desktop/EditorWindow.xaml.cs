using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using Rcm.Contracts;

namespace Rcm.Desktop;
public partial class EditorWindow : Window
{
    public EditorViewModel Model { get; }
    private readonly CrmClient api;
    private CancellationTokenSource? saveCancellation;
    private readonly CancellationTokenSource lifetime = new();
    public EditorWindow(CrmClient api, EditorViewModel model)
    {
        this.api = api; Model = model; DataContext = model; InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        States.ItemsSource = CrmVocabulary.States;
        Planning.Visibility = model.IsCustomer ? Visibility.Collapsed : Visibility.Visible;
        Original.IsReadOnly = model.Customer is not null; Synthetic.IsEnabled = model.Customer is null;
        Loaded += (_, _) => { if (model.IsCustomer) CustomerName.Focus(); else if (model.IsConversation) Note.Focus(); };
        Closing += GuardClose; Closed += (_, _) => lifetime.Cancel();
    }
    private void GuardClose(object? sender, CancelEventArgs e)
    {
        if (Model.IsSaving) { e.Cancel = true; Model.Status = "Zapis trwa. Poczekaj lub zatrzymaj oczekiwanie."; return; }
        if (Model.Dirty && MessageBox.Show(this, Model.IsUncertain ? "Wynik zapisu jest nieznany. Ponowienie w tym formularzu jest bezpieczne. Zamknąć i utracić możliwość ponowienia?" : "Masz niezapisane zmiany. Odrzucić je i zamknąć?", "Niezapisane zmiany", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Model.IsSaving) return;
        if (Model.ExactDate && !DateTime.TryParse(ContactDate.Text, out _))
        { Model.Status = "Wpisz poprawną datę lub wybierz termin opisowy."; ContactDate.Focus(); return; }
        saveCancellation?.Dispose(); saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            if (!await Model.Save(api, saveCancellation.Token)) return;
            if (Model.Saved is SaveCustomerResult { PossibleDuplicates.Length: > 0 } result)
                MessageBox.Show(this, "Zapisano. Podobne dane kontaktowe mają:\n" + string.Join("\n", result.PossibleDuplicates.Select(c => c.Fields.DisplayName)) + "\nRekordy nie zostały połączone.", "Możliwe duplikaty");
            DialogResult = true;
        }
        catch (ApiFailure ex) when (ex.Status == 401)
        {
            new LoginWindow(api) { Owner = this }.ShowDialog();
            Model.Status = "Formularz zachowany. Po zalogowaniu wybierz Zapisz.";
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Stop_Click(object sender, RoutedEventArgs e) => saveCancellation?.Cancel();
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await Model.LoadComparison(api, lifetime.Token); }
        catch (ApiFailure ex) { Model.Status = ex.Message; if (ex.Status == 401) new LoginWindow(api) { Owner = this }.ShowDialog(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Model.Status = "Nie można pobrać danych do porównania. Formularz zachowany."; }
    }
    private void AcceptVersion_Click(object sender, RoutedEventArgs e) => Model.AcceptComparedVersion();
}
