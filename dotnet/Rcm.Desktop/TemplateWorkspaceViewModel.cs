using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum TemplateWorkspaceMode { Sop, Projects, Catalog }
public sealed record TemplateListRow(ProductTemplateDto Template, bool Office)
{
    public string Name => Template.Name;
    public string Category => Template.Category;
    public string Project => Template.ProjectCode ?? "—";
    public string Position => Template.PositionNumber ?? "—";
    public string Group => Template.ProjectCode ?? Template.Category;
    public string Price => Office && !string.IsNullOrEmpty(Template.ProjectCode) ? "Do wyceny" : Template.BasePricePln is { } amount ? $"{amount:N2} zł" : "—";
    public string State => Template.IsActive ? "Aktywny" : "Archiwum";
}

public sealed partial class TemplateWorkspaceViewModel(CrmClient api) : ObservableObject
{
    private int requestVersion;
    public TemplateFeatures Features { get; private set; } = new(false, false, false, false, false, 90);
    [ObservableProperty] private TemplateWorkspaceMode mode;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string projectSearch = "";
    [ObservableProperty] private string category = "";
    [ObservableProperty] private bool includeArchived;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private int projectPage = 1;
    [ObservableProperty] private int projectTotal;
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private string projectInfo = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private TemplateListRow? selected;
    [ObservableProperty] private TemplateProjectDto? selectedProject;
    public ObservableCollection<TemplateListRow> Rows { get; } = [];
    public ObservableCollection<TemplateProjectDto> Projects { get; } = [];
    public bool IsProjects => Mode == TemplateWorkspaceMode.Projects;
    public bool IsAdmin => Mode != TemplateWorkspaceMode.Catalog && api.Session?.Role == "technolog";
    public string Heading => Mode switch { TemplateWorkspaceMode.Sop => "Katalog SOP", TemplateWorkspaceMode.Projects => "Projekty", _ => "Katalog usług i produktów" };
    public string Description => IsProjects ? "Pozycje projektów, operacje, materiały i dokumentacja produkcyjna." : Mode == TemplateWorkspaceMode.Sop ? "Szablony, instrukcje i dane używane przy realizacji zleceń." : "Usługi i produkty pogrupowane według projektu i kategorii.";
    public string ProjectHeading => SelectedProject is { } project ? $"{project.Code} · {project.PositionsCount} pozycji" : "Wybierz projekt";
    public bool CanCreate => IsAdmin && Features.Write && Mode == TemplateWorkspaceMode.Sop && !Busy;
    public bool CanOpen => Features.Read && Selected is not null && !Busy;
    public bool CanPrevious => !Busy && Page > 1;
    public bool CanNext => !Busy && Page * 50 < Total;
    public bool CanProjectPrevious => !Busy && ProjectPage > 1;
    public bool CanProjectNext => !Busy && ProjectPage * 50 < ProjectTotal;
    public bool CanDownloadProject => IsProjects && Features.Documents && api.Session?.Role == "technolog" && SelectedProject is not null && !Busy;
    public string AccessHint => !Features.Read ? "Ten serwer nie obsługuje jeszcze katalogu szablonów." : !IsAdmin || !Features.Write ? "Podgląd katalogu — tylko do odczytu." : "Zmiany zapisujesz w formularzu wybranego szablonu.";
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnModeChanged(TemplateWorkspaceMode value) => Notify();
    partial void OnSelectedChanged(TemplateListRow? value) => Notify();
    partial void OnSelectedProjectChanged(TemplateProjectDto? value) { OnPropertyChanged(nameof(ProjectHeading)); Notify(); }
    partial void OnPageChanged(int value) => Notify();
    partial void OnTotalChanged(int value) => Notify();
    partial void OnProjectPageChanged(int value) => Notify();
    partial void OnProjectTotalChanged(int value) => Notify();
    public void Notify()
    {
        foreach (var name in new[] { nameof(IsProjects), nameof(IsAdmin), nameof(Heading), nameof(Description), nameof(CanCreate), nameof(CanOpen), nameof(CanPrevious), nameof(CanNext), nameof(CanProjectPrevious), nameof(CanProjectNext), nameof(CanDownloadProject), nameof(AccessHint) }) OnPropertyChanged(name);
    }
    public void SetFeatures(TemplateFeatures value) { Features = value; Notify(); }
    public async Task LoadProjects(CancellationToken ct)
    {
        if (!Features.Read) return;
        var version = Interlocked.Increment(ref requestVersion); var code = SelectedProject?.Code;
        Busy = true; Status = "Wczytywanie projektów…";
        try
        {
            var result = await api.ReadTemplateProjects($"?q={Uri.EscapeDataString(ProjectSearch)}&page={ProjectPage}&pageSize=50", ct);
            ct.ThrowIfCancellationRequested(); if (version != requestVersion) return;
            Projects.Clear(); foreach (var row in result.Items) Projects.Add(row);
            SelectedProject = result.Items.FirstOrDefault(row => row.Code == code) ?? result.Items.FirstOrDefault();
            ProjectTotal = result.Total; ProjectPage = result.PageNumber; ProjectInfo = $"Strona {ProjectPage} · {ProjectTotal} projektów";
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public async Task Load(CancellationToken ct)
    {
        if (!Features.Read) return;
        if (IsProjects && SelectedProject is null) { Rows.Clear(); Selected = null; Total = 0; PageInfo = ""; Status = "Brak projektów w katalogu."; return; }
        var version = Interlocked.Increment(ref requestVersion); var selectedId = Selected?.Template.Id;
        Busy = true; Status = "Wczytywanie szablonów…";
        try
        {
            var project = IsProjects ? SelectedProject!.Code : "";
            var result = await api.ReadTemplates<Page<ProductTemplateDto>>($"?q={Uri.EscapeDataString(Search)}&category={Uri.EscapeDataString(Category)}&projectCode={Uri.EscapeDataString(project)}&includeArchived={(IsAdmin && IncludeArchived).ToString().ToLowerInvariant()}&page={Page}&pageSize=50", ct);
            ct.ThrowIfCancellationRequested(); if (version != requestVersion) return;
            if (result.Items.Any(row => row.Id <= 0 || row.Version <= 0 || IsProjects && row.ProjectCode != project)) throw new ApiFailure(502, "Nieprawidłowa lista szablonów projektu.");
            Rows.Clear(); foreach (var row in result.Items) Rows.Add(new(row, Mode == TemplateWorkspaceMode.Catalog));
            Selected = Rows.FirstOrDefault(row => row.Template.Id == selectedId);
            Total = result.Total; Page = result.PageNumber; PageInfo = $"Strona {Page} · {Total} pozycji";
            Status = Rows.Count == 0 ? "Brak pozycji spełniających kryteria." : "Dane aktualne.";
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public async Task<ProductTemplateDto?> Open(CancellationToken ct)
    {
        if (!CanOpen || Selected is not { } row) return null;
        var version = Interlocked.Increment(ref requestVersion); Busy = true;
        try
        {
            var result = await api.ReadTemplates<ProductTemplateDto>($"/{row.Template.Id}", ct);
            ct.ThrowIfCancellationRequested(); if (version != requestVersion) return null;
            if (result.Id != row.Template.Id) throw new ApiFailure(502, "Nieprawidłowy szablon."); return result;
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public async Task DownloadProject(CrmClient api, string destination, CancellationToken ct)
    {
        if (!CanDownloadProject || SelectedProject is not { } project) return;
        Busy = true;
        try { await api.DownloadTemplateProject(project.Code, destination, ct); Status = $"Zapisano arkusze projektu: {destination}"; }
        finally { Busy = false; }
    }
    public void CancelRead() { Interlocked.Increment(ref requestVersion); Busy = false; }
    public void Clear()
    {
        CancelRead(); Rows.Clear(); Projects.Clear(); Selected = null; SelectedProject = null;
        Search = ProjectSearch = Category = PageInfo = ProjectInfo = Status = ""; Page = ProjectPage = 1; Total = ProjectTotal = 0; IncludeArchived = false;
        SetFeatures(new(false, false, false, false, false, 90));
    }
}
