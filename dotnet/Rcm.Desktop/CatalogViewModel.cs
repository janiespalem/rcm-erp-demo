using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum CatalogKind { Materials, Operations }
public sealed record CatalogRow(CatalogKind Kind, long Id, long Version, string Name, string? Group, double? Rate, bool IsActive, string? Notes, string? Formula)
{
    public string RateLabel => Rate?.ToString("N2") ?? "—";
    public string StateLabel => IsActive ? "Aktywny" : "Archiwum";
    public static CatalogRow From(CatalogMaterialDto item) => new(CatalogKind.Materials, item.Id, item.Version, item.Name, item.Category, item.DefaultRatePlnKg, item.IsActive, item.Notes, null);
    public static CatalogRow From(CatalogOperationDto item) => new(CatalogKind.Operations, item.Id, item.Version, item.Name, item.Department, item.DefaultRate, true, null, item.Formula);
}

public sealed partial class CatalogViewModel(CrmClient api, CatalogKind kind) : ObservableObject
{
    private int requestVersion;
    public CatalogKind Kind { get; } = kind;
    public bool IsMaterials => Kind == CatalogKind.Materials;
    public string Path => IsMaterials ? "/materials" : "/operations";
    public string Heading => IsMaterials ? "Materiały" : "Operacje i usługi";
    public string Description => IsMaterials ? "Nazwy, wymiary i stawki materiałów używane w zleceniach i wycenach." : "Operacje, wydziały i stawki robocizny używane w wycenach.";
    public string GroupLabel => IsMaterials ? "Kategoria" : "Wydział";
    public string RateLabel => IsMaterials ? "PLN/kg" : "PLN/h";
    public string RemoveLabel => IsMaterials ? "Archiwizuj" : "Usuń";
    public string AccessHint => !Available ? "Katalog jest niedostępny na tym serwerze." : !WriteEnabled ? "Katalog tylko do odczytu. Zapis będzie dostępny po uruchomieniu obsługi natywnej." : "Zmiany są dostępne dla technologa.";
    [ObservableProperty] private bool available;
    [ObservableProperty] private bool writeEnabled;
    [ObservableProperty] private double laborRate = 90;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool failed;
    [ObservableProperty] private bool includeArchived;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private CatalogRow? selected;
    public ObservableCollection<CatalogRow> Rows { get; } = [];
    public bool CanWrite => Available && WriteEnabled && api.Session?.Role == "technolog" && !Busy;
    public bool CanEdit => CanWrite && Selected is not null;
    public bool CanRemove => CanEdit && Selected!.IsActive;
    public bool CanPrevious => Available && !Busy && Page > 1;
    public bool CanNext => Available && !Busy && Page * 50 < Total;
    partial void OnSelectedChanged(CatalogRow? value) => NotifyActions();
    partial void OnBusyChanged(bool value) => NotifyActions();
    partial void OnPageChanged(int value) => NotifyActions();
    partial void OnTotalChanged(int value) => NotifyActions();
    partial void OnAvailableChanged(bool value) { OnPropertyChanged(nameof(AccessHint)); NotifyActions(); }
    partial void OnWriteEnabledChanged(bool value) { OnPropertyChanged(nameof(AccessHint)); NotifyActions(); }
    public void NotifyActions()
    {
        foreach (var name in new[] { nameof(CanWrite), nameof(CanEdit), nameof(CanRemove), nameof(CanPrevious), nameof(CanNext) }) OnPropertyChanged(name);
    }
    public async Task Load(CancellationToken ct)
    {
        if (!Available) return;
        var version = Interlocked.Increment(ref requestVersion);
        var selectedId = Selected?.Id;
        Busy = true; Failed = false; Status = "Wczytywanie katalogu…";
        try
        {
            var query = $"{Path}?q={Uri.EscapeDataString(Search)}&includeArchived={IncludeArchived.ToString().ToLowerInvariant()}&page={Page}&pageSize=50";
            CatalogRow[] rows; int total; int page;
            if (IsMaterials)
            {
                var result = await api.ReadCatalog<Page<CatalogMaterialDto>>(query, ct);
                rows = result.Items.Select(CatalogRow.From).ToArray(); total = result.Total; page = result.PageNumber;
            }
            else
            {
                var result = await api.ReadCatalog<Page<CatalogOperationDto>>(query, ct);
                rows = result.Items.Select(CatalogRow.From).ToArray(); total = result.Total; page = result.PageNumber;
            }
            ct.ThrowIfCancellationRequested();
            if (version != requestVersion) return;
            if (rows.Any(row => row.Id <= 0 || row.Version <= 0)) throw new ApiFailure(502, "Nieprawidłowe dane katalogu.");
            Rows.Clear(); foreach (var row in rows) Rows.Add(row);
            Selected = rows.FirstOrDefault(row => row.Id == selectedId);
            Total = total; Page = page; PageInfo = $"Strona {page} · {total} pozycji";
            Status = rows.Length == 0 ? "Brak pozycji spełniających kryteria." : "Dane aktualne.";
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public void CancelRead() { Interlocked.Increment(ref requestVersion); Busy = false; }
    public void Clear()
    {
        CancelRead(); Rows.Clear(); Selected = null; Search = Status = PageInfo = "";
        Page = 1; Total = 0; Failed = IncludeArchived = Available = WriteEnabled = false; LaborRate = 90;
    }
}
