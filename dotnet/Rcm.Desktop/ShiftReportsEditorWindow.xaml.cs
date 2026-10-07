using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Rcm.Desktop;

public partial class ShiftReportsEditorWindow : Window
{
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    public ShiftReportsEditorViewModel Model { get; }
    public ShiftReportsEditorWindow(CrmClient api, ShiftReportsEditorViewModel model)
    {
        this.api = api; Model = model; Model.RefreshAccess(api); InitializeComponent(); DataContext = Model;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.OperationBusy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj lub zatrzymaj oczekiwanie."; }
            else if (Model.Dirty && MessageBox.Show(this, Model.OperationUncertain ? "Wynik operacji nieznany. Ponów tę samą operację przed zamknięciem. Zamknąć bez potwierdzenia?" : "Odrzucić niezapisane dane lub otwartą korektę?", "Niezapisany raport zmiany", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Loaded += async (_, _) => await Run(ct => Model.Production.Initialize(api, ct));
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.RefreshAccess(api);
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || lifetime.IsCancellationRequested) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation = request;
        try { var version = Model.Current?.Version; await action(request.Token); if (Model.Removed) Close(); else if (Model.Current?.Version != version) await Model.Production.Refresh(request.Token); }
        catch (ApiFailure ex) { if (!lifetime.IsCancellationRequested) { Model.Status = ex.Message + " Formularz zachowano."; if (ex.Status == 401) { new LoginWindow(api) { Owner = this }.ShowDialog(); Model.RefreshAccess(api); } } }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) Model.Status = "Nie otrzymano odpowiedzi. Formularz zachowano."; }
        finally { operation = null; }
    }
    private async void Create_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Create(api, ct); });
    private async void Save_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Save(api, ct); });
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Retry(api, ct); });
    private async void Finalize_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanFinalize || MessageBox.Show(this, Model.Production.State?.Link is not null ? "Przekazać raport z przypisanym kontraktem do sprawdzenia?" : "Zakończyć raport po sprawdzeniu wszystkich pól?", "Zakończenie raportu", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.FinalizeReport(api, ct); });
    }
    private void StartCorrection_Click(object sender, RoutedEventArgs e) => Model.StartCorrection();
    private void CancelCorrection_Click(object sender, RoutedEventArgs e) { if (Model.Correction && !Model.Busy && !Model.Uncertain && MessageBox.Show(this, "Odrzucić otwartą korektę?", "Odrzucenie korekty", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) Model.CancelCorrection(); }
    private async void Correct_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Correct(api, ct); });
    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanDiscard || MessageBox.Show(this, "Trwale usunąć niedokończony własny szkic?", "Usunięcie szkicu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.Discard(api, ct); });
    }
    private async void AdminDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanAdminDelete || MessageBox.Show(this, "Usunąć raport administracyjnie z podanym powodem?", "Usunięcie raportu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.AdminDelete(api, ct); });
    }
    private async void Compare_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Compare(api, ct));
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptComparison();
    private async void Audit_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.ReadAudit(api, ct));
    private async void AuditFilter_Click(object sender, RoutedEventArgs e) { if (IsLoaded && Model.CanReadAudit) await Run(ct => Model.ReadAudit(api, ct)); }
    private void PreviewPrint_Click(object sender, RoutedEventArgs e) => ShiftPrintPreview.Document = ShiftReportsPrint.Create(Model);
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanPrint) return;
        try
        {
        var dialog = new PrintDialog(); if (dialog.ShowDialog() != true) return;
        var document = ShiftReportsPrint.Create(Model); dialog.PrintTicket.PageMediaSize = new System.Printing.PageMediaSize(System.Printing.PageMediaSizeName.ISOA4);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, Model.Heading);
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or InvalidOperationException or System.ComponentModel.Win32Exception) { Model.Status = "Nie udało się wydrukować raportu. Sprawdź drukarkę i spróbuj ponownie."; }
    }
    private async void ProductionRefresh_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Production.Initialize(api, ct));
    private void OpenProduction_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.Production.CanRead) return;
        ShiftReportTabs.SelectedItem = ReportProductionTab;
        if (Model.Production.CanChoose) ReportContractSelection.Focus();
        else if (Model.Production.CanReview) ReportReviewReason.Focus();
    }
    private void OpenForm_Click(object sender, RoutedEventArgs e) => ShiftReportTabs.SelectedItem = ReportFormTab;
    private async void ProductionSearch_Click(object sender, RoutedEventArgs e) { Model.Production.ContractPage = 1; await Run(ct => Model.Production.LoadContracts(ct)); }
    private async void ProductionPreviousContract_Click(object sender, RoutedEventArgs e) { if (Model.Production.CanPreviousContract) { Model.Production.ContractPage--; await Run(ct => Model.Production.LoadContracts(ct)); } }
    private async void ProductionNextContract_Click(object sender, RoutedEventArgs e) { if (Model.Production.CanNextContract) { Model.Production.ContractPage++; await Run(ct => Model.Production.LoadContracts(ct)); } }
    private async void ProductionLink_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Production.Link(ct); });
    private async void ProductionRetry_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Production.Retry(ct); });
    private async void ProductionCompare_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Production.Compare(ct));
    private void ProductionAcceptComparison_Click(object sender, RoutedEventArgs e) => Model.Production.AcceptComparison();
    private async void ProductionAccept_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.Production.CanReview || MessageBox.Show(this, "Przyjąć tę wersję raportu dla przypisanego kontraktu?", "Przyjęcie raportu", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.Production.Review("accepted", ct); });
    }
    private async void ProductionReturn_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Production.Review("returned", ct); });
    private async void ProductionAudit_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Production.ReadAudit(ct));
    private async void ProductionPreviousAudit_Click(object sender, RoutedEventArgs e) { if (Model.Production.CanPreviousAudit) { Model.Production.AuditPage--; await Run(ct => Model.Production.ReadAudit(ct)); } }
    private async void ProductionNextAudit_Click(object sender, RoutedEventArgs e) { if (Model.Production.CanNextAudit) { Model.Production.AuditPage++; await Run(ct => Model.Production.ReadAudit(ct)); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
