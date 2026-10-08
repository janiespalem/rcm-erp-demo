using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class TemplateWorkspace : UserControl
{
    private CrmClient? api;
    private CancellationTokenSource request = new();
    private bool ready;
    private bool syncing;
    public TemplateWorkspaceViewModel Model { get; private set; } = null!;
    public TemplateWorkspace()
    { InitializeComponent(); IsVisibleChanged += (_, _) => { if (ready && !IsVisible) CancelRead(); }; }
    public void SetClient(CrmClient client) { api = client; Model = new(client); DataContext = Model; ready = true; }
    public void CancelRead() { request.Cancel(); if (ready) Model.CancelRead(); }
    public void Clear() { CancelRead(); syncing = true; try { Model.Clear(); } finally { syncing = false; } }
    public void SetMode(TemplateWorkspaceMode mode)
    {
        if (Model.Mode == mode) return;
        Clear(); Model.Mode = mode;
    }
    private async Task Run(Func<CancellationToken, Task> action, bool debounce = false)
    {
        if (!ready || api is null) return;
        request.Cancel(); request.Dispose(); request = new(); var ct = request.Token;
        try { if (debounce) await Task.Delay(300, ct); await action(ct); }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Model.Status = "Upłynął czas oczekiwania. Odśwież dane."; }
        catch (ApiFailure ex) { if (!ct.IsCancellationRequested) { Model.Status = ex.Message; if (ex.Status == 401) new LoginWindow(api) { Owner = Window.GetWindow(this) }.ShowDialog(); } }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Model.Status = "Brak połączenia. Spróbuj ponownie."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!ct.IsCancellationRequested) Model.Status = "Nie udało się zapisać dokumentu. Sprawdź uprawnienia i wolne miejsce."; }
        finally { if (!ct.IsCancellationRequested) Model.Notify(); }
    }
    private async Task LoadProjects(CancellationToken ct)
    { syncing = true; try { await Model.LoadProjects(ct); } finally { syncing = false; } await Model.Load(ct); }
    public Task Activate() => Run(async ct =>
    {
        try { var features = await api!.ReadTemplates<TemplateFeatures>("/features", ct); ct.ThrowIfCancellationRequested(); Model.SetFeatures(features); }
        catch (ApiFailure ex) when (ex.Status == 404) { ct.ThrowIfCancellationRequested(); Model.SetFeatures(new(false, false, false, false, false, 90)); }
        if (Model.IsProjects) await LoadProjects(ct); else await Model.Load(ct);
    });
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct), true); } }
    private async void ProjectSearch_Changed(object sender, TextChangedEventArgs e) { if (ready && !syncing) { Model.ProjectPage = Model.Page = 1; await Run(LoadProjects, true); } }
    private async void Project_Changed(object sender, SelectionChangedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct)); } }
    private async void Archive_Changed(object sender, RoutedEventArgs e) { if (ready && !syncing) { Model.Page = 1; await Run(ct => Model.Load(ct)); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Activate();
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.CanPrevious) { Model.Page--; await Run(ct => Model.Load(ct)); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.CanNext) { Model.Page++; await Run(ct => Model.Load(ct)); } }
    private async void ProjectsPrevious_Click(object sender, RoutedEventArgs e) { if (Model.CanProjectPrevious) { Model.ProjectPage--; await Run(LoadProjects); } }
    private async void ProjectsNext_Click(object sender, RoutedEventArgs e) { if (Model.CanProjectNext) { Model.ProjectPage++; await Run(LoadProjects); } }
    private void Rows_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
    private async Task Edit(ProductTemplateDto? template, CancellationToken ct)
    {
        if (api is null) return;
        var editor = new TemplateEditorViewModel(template, Model.Features with { Write = Model.IsAdmin && Model.Features.Write }, Model.IsProjects);
        new TemplateEditorWindow(api, editor) { Owner = Window.GetWindow(this) }.ShowDialog();
        if (editor.Committed && !ct.IsCancellationRequested) { await Model.Load(ct); Model.Status = editor.Status; }
    }
    private async void Create_Click(object sender, RoutedEventArgs e) { if (Model.CanCreate) await Run(ct => Edit(null, ct)); }
    private async void Open_Click(object sender, RoutedEventArgs e)
    { if (Model.CanOpen) await Run(async ct => { if (await Model.Open(ct) is { } template) await Edit(template, ct); }); }
    private async void ProjectDocument_Click(object sender, RoutedEventArgs e)
    {
        if (api is null || !Model.CanDownloadProject || Model.SelectedProject is not { } project) return;
        var dialog = new SaveFileDialog { Title = "Zapisz arkusze projektu", FileName = OrderAttachmentsViewModel.SafeFilename($"{project.Code.Replace('/', '_')}_arkusze.pdf", "Projekt_arkusze"), Filter = "Dokument PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, CheckPathExists = true, OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await Run(ct => Model.DownloadProject(api, dialog.FileName, ct));
    }
}
