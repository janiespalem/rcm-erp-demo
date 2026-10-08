using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Rcm.Desktop;

public partial class TemplateEditorWindow : Window
{
    public TemplateEditorViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    public TemplateEditorWindow(CrmClient api, TemplateEditorViewModel model)
    {
        this.api = api; Model = model; Model.RefreshAccess(api); InitializeComponent(); DataContext = Model;
        SopLibrary.ItemsSource = TemplateEditorViewModel.SopLibrary;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height); Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Operacja trwa. Poczekaj lub zatrzymaj oczekiwanie."; }
            else if (Model.Dirty && MessageBox.Show(this, Model.Uncertain
                ? "Wynik operacji jest nieznany. Ponów tę samą operację przed zamknięciem. Zamknąć bez potwierdzenia?"
                : "Odrzucić niezapisane dane lub wybrany rysunek?", "Niezapisany szablon SOP", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.RefreshAccess(api);
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || lifetime.IsCancellationRequested) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation = request;
        try { await action(request.Token); }
        catch (ApiFailure ex)
        {
            if (lifetime.IsCancellationRequested) return;
            Model.Status = ex.Message + " Dane formularza zachowano.";
            if (ex.Status == 401) { new LoginWindow(api) { Owner = this }.ShowDialog(); Model.RefreshAccess(api); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie otrzymano odpowiedzi. Dane formularza zachowano."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!lifetime.IsCancellationRequested) Model.Status = "Nie udało się odczytać lub zapisać pliku. Sprawdź uprawnienia i wolne miejsce."; }
        finally { operation = null; }
    }
    private void CommitRows() { foreach (var grid in new[] { TemplateOperations, TemplateMaterials, TemplateInstructions, TemplateMachines }) { grid.CommitEdit(); grid.CommitEdit(DataGridEditingUnit.Row, true); } }
    private async void Save_Click(object sender, RoutedEventArgs e) { CommitRows(); await Run(async ct => { await Model.Save(api, ct); }); }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Retry(api, ct); });
    private async void Compare_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.Compare(api, ct));
    private void Accept_Click(object sender, RoutedEventArgs e) => Model.AcceptComparison();
    private async void Lifecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanLifecycle || MessageBox.Show(this, $"{Model.LifecycleLabel}: {Model.Name}?", "Stan szablonu", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.Lifecycle(api, ct); });
    }
    private async void ChooseDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanChooseDrawing) return;
        var dialog = new OpenFileDialog { Title = "Wybierz rysunek PDF", Filter = "Rysunek PDF (*.pdf)|*.pdf", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) await Run(ct => Model.SelectDrawing(dialog.FileName, ct));
    }
    private async void Upload_Click(object sender, RoutedEventArgs e) => await Run(async ct => { await Model.Upload(api, ct); });
    private void DiscardDrawing_Click(object sender, RoutedEventArgs e) => Model.DiscardDrawing();
    private async void Preview_Click(object sender, RoutedEventArgs e) => await Run(ct => Model.PreviewDrawing(api, ct));
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanApply || MessageBox.Show(this, "Zastąpić dane szablonu sprawdzonym odczytem z rysunku PDF?", "Zastosuj odczyt", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct => { await Model.Apply(api, ct); });
    }
    private async Task Download(bool drawing)
    {
        if (Model.Current is not { } template || (drawing ? !Model.CanDownloadDrawing : !Model.CanDownloadSheet)) return;
        var dialog = new SaveFileDialog { Title = "Zapisz dokument szablonu", FileName = $"{(drawing ? "Rysunek" : "Arkusz")}_{template.Id}.pdf", Filter = "Dokument PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true, CheckPathExists = true };
        if (dialog.ShowDialog(this) == true) await Run(ct => Model.Download(api, drawing, dialog.FileName, ct));
    }
    private async void DownloadDrawing_Click(object sender, RoutedEventArgs e) => await Download(true);
    private async void DownloadSheet_Click(object sender, RoutedEventArgs e) => await Download(false);
    private void AddOperation_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) Model.Operations.Add(new(laborRate: Model.LaborRate)); }
    private void RemoveOperation_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit && TemplateOperations.SelectedItem is TemplateOperationRow row) Model.Operations.Remove(row); }
    private void AddMaterial_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) Model.Materials.Add(new()); }
    private void RemoveMaterial_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit && TemplateMaterials.SelectedItem is TemplateMaterialRow row) Model.Materials.Remove(row); }
    private void AddInstruction_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) Model.Instructions.Add(new(index: Model.Instructions.Count + 1) { Text = SopLibrary.SelectedItem as string ?? "" }); }
    private void Renumber() { for (var index = 0; index < Model.Instructions.Count; index++) Model.Instructions[index].Sequence = (index + 1).ToString(); }
    private void RemoveInstruction_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit && TemplateInstructions.SelectedItem is TemplateInstructionRow row) { Model.Instructions.Remove(row); Renumber(); } }
    private void MoveInstruction_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanEdit || TemplateInstructions.SelectedItem is not TemplateInstructionRow row || sender is not Button { Tag: string tag } || !int.TryParse(tag, out var delta)) return;
        var index = Model.Instructions.IndexOf(row); var target = index + delta;
        if (target >= 0 && target < Model.Instructions.Count) { Model.Instructions.Move(index, target); Renumber(); }
    }
    private void AddMachine_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit) Model.Machines.Add(new()); }
    private void RemoveMachine_Click(object sender, RoutedEventArgs e) { if (Model.CanEdit && TemplateMachines.SelectedItem is TemplateMachineRow row) Model.Machines.Remove(row); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
