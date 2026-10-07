using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class CatalogEditorViewModel : ObservableObject
{
    private readonly CatalogRow? original;
    private readonly string initial;
    private long version;
    private object? pending;
    private CatalogRow? compared;
    private bool blocked;
    public CatalogKind Kind { get; }
    public bool IsMaterials => Kind == CatalogKind.Materials;
    public bool IsOperations => !IsMaterials;
    public bool IsNew => original is null;
    public bool IsRemoval { get; }
    public bool IsForm => !IsRemoval;
    public bool Completed { get; private set; }
    public CatalogRow? SavedRow { get; private set; }
    private string Path => IsMaterials ? "/materials" : "/operations";
    public string Heading => IsRemoval ? IsMaterials ? "Archiwizuj materiał" : "Usuń operację" : IsNew ? IsMaterials ? "Nowy materiał" : "Nowa operacja / usługa" : IsMaterials ? "Edytuj materiał" : "Edytuj operację / usługę";
    public string GroupLabel => IsMaterials ? "Kategoria" : "Wydział";
    public string RateLabel => IsMaterials ? "Stawka PLN/kg" : "Stawka PLN/h";
    public string RemovalHint => IsMaterials ? "Materiał zniknie z aktywnego katalogu. Dane istniejących zleceń i wycen zostaną zachowane." : "Operacja zniknie z katalogu. Dane istniejących zleceń i wycen zostaną zachowane.";
    public bool Dirty => !Completed && (Snapshot() != initial || Uncertain);
    private bool Allowed => Role == "technolog" && WriteEnabled && !blocked;
    public bool CanEdit => IsForm && !Busy && !Uncertain && !Completed && Allowed;
    public bool CanSave => !Busy && !HasConflict && !Completed && Allowed;
    public bool CanCompare => HasConflict && !Busy;
    public bool CanAccept => compared is not null && !Busy;
    public string SaveLabel => Uncertain ? "Ponów ten sam zapis" : IsRemoval ? IsMaterials ? "Archiwizuj materiał" : "Usuń operację" : "Zapisz";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string group = "";
    [ObservableProperty] private string rate = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string formula = "";
    [ObservableProperty] private bool active = true;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool writeEnabled;
    [ObservableProperty] private string? role;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comparison = "";
    [ObservableProperty] private string nameError = "";
    [ObservableProperty] private string groupError = "";
    [ObservableProperty] private string rateError = "";
    public ObservableCollection<string> Errors { get; } = [];
    public CatalogEditorViewModel(CatalogKind kind, CatalogRow? row = null, bool remove = false, double laborRate = 90)
    {
        if (row is not null && row.Kind != kind || remove && row is null) throw new ArgumentException("Nieprawidłowa pozycja katalogu.", nameof(row));
        Kind = kind; original = row; version = row?.Version ?? 0; IsRemoval = remove;
        Name = row?.Name ?? ""; Group = row?.Group ?? (IsMaterials ? "zbrojenie" : "");
        Rate = row is null ? (IsMaterials ? 4.5 : laborRate).ToString(CultureInfo.CurrentCulture) : row.Rate?.ToString(CultureInfo.CurrentCulture) ?? "";
        Notes = row?.Notes ?? ""; Formula = row?.Formula ?? ""; Active = row?.IsActive ?? true;
        initial = Snapshot();
        if (IsRemoval) Status = $"Potwierdź operację dla: {Name}.";
    }
    private string Snapshot() => JsonSerializer.Serialize(new { Name, Group, Rate, Notes, Formula, Active });
    private void InputChanged() { if (!Busy && !Uncertain) pending = null; Notify(); }
    partial void OnNameChanged(string value) => InputChanged();
    partial void OnGroupChanged(string value) => InputChanged();
    partial void OnRateChanged(string value) => InputChanged();
    partial void OnNotesChanged(string value) => InputChanged();
    partial void OnFormulaChanged(string value) => InputChanged();
    partial void OnActiveChanged(bool value) => InputChanged();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    partial void OnHasConflictChanged(bool value) => Notify();
    partial void OnRoleChanged(string? value) => Notify();
    partial void OnWriteEnabledChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(Dirty), nameof(CanEdit), nameof(CanSave), nameof(CanCompare), nameof(CanAccept), nameof(SaveLabel) }) OnPropertyChanged(name);
    }
    public void RefreshAccess(CrmClient api) => Role = api.Session?.Role;
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private bool Validate(out double? amount)
    {
        amount = null; Errors.Clear(); NameError = GroupError = RateError = "";
        if (Name.Trim().Length is < 1 or > 100) NameError = "Wpisz nazwę od 1 do 100 znaków.";
        if (Group.Trim().Length > 50) GroupError = "Wpisz maksymalnie 50 znaków.";
        if (!string.IsNullOrWhiteSpace(Rate))
        {
            var max = IsMaterials ? 9999.99 : 1_000_000;
            if (!OrderEditorViewModel.TryNumber(Rate, out var value) || value < 0 || value > max)
                RateError = $"Podaj liczbę od 0 do {max:N2} lub pozostaw pole puste.";
            else amount = value;
        }
        if (Notes.Length > 10000) Errors.Add("Uwagi: wpisz maksymalnie 10 000 znaków.");
        if (Formula.Trim().Length > 255) Errors.Add("Słowa do podpowiedzi: wpisz maksymalnie 255 znaków.");
        return NameError.Length == 0 && GroupError.Length == 0 && RateError.Length == 0 && Errors.Count == 0;
    }
    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        RefreshAccess(api);
        if (!CanSave) return false;
        if (pending is null)
        {
            if (IsRemoval) pending = new CatalogVersionCommand(Guid.NewGuid(), version);
            else
            {
                if (!Validate(out var amount)) { Status = "Popraw zaznaczone pola. Wpisane dane zachowano."; return false; }
                var id = Guid.NewGuid();
                pending = IsMaterials
                    ? IsNew ? new CreateCatalogMaterial(id, Name.Trim(), Empty(Group), amount, Active, Empty(Notes))
                        : new UpdateCatalogMaterial(id, version, Name.Trim(), Empty(Group), amount, Active, Empty(Notes))
                    : IsNew ? new CreateCatalogOperation(id, Name.Trim(), Empty(Group), amount, Empty(Formula))
                        : new UpdateCatalogOperation(id, version, Name.Trim(), Empty(Group), amount, Empty(Formula));
            }
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            if (IsRemoval)
            {
                var result = await api.WriteCatalog<CatalogMutationResult>($"{Path}/{original!.Id}/{(IsMaterials ? "archive" : "delete")}", pending, ct);
                if (result.Id != original.Id || result.Version <= version || result.Deleted != IsOperations)
                    throw new ApiFailure(502, "Nieprawidłowe potwierdzenie operacji.");
            }
            else
            {
                var path = Path + (IsNew ? "" : $"/{original!.Id}/update");
                SavedRow = IsMaterials
                    ? CatalogRow.From(await api.WriteCatalog<CatalogMaterialDto>(path, pending, ct))
                    : CatalogRow.From(await api.WriteCatalog<CatalogOperationDto>(path, pending, ct));
                if (SavedRow.Id <= 0 || SavedRow.Version <= version || original is not null && SavedRow.Id != original.Id)
                    throw new ApiFailure(502, "Nieprawidłowe potwierdzenie zapisu.");
            }
            Completed = true; pending = null; Uncertain = false;
            Status = IsRemoval ? IsMaterials ? "Materiał zarchiwizowany." : "Operacja usunięta." : "Zapisano pozycję katalogu.";
            Notify(); return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401)
        { MarkUncertain("Zaloguj się ponownie i ponów ten sam zapis. Wpisane dane zachowano."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500)
        { MarkUncertain("Brak potwierdzenia. Ponów ten sam zapis; dane i identyfikator zachowano."); return false; }
        catch (ApiFailure ex)
        {
            if (ex.Status == 409)
            { pending = null; Uncertain = false; HasConflict = true; compared = null; Comparison = ""; Status = ex.Message + " Porównaj z aktualną wersją. Twoje dane zachowano."; }
            else
            {
                if (!Uncertain) pending = null;
                blocked = ex.Status is 403 or 404 or 410;
                Errors.Clear(); foreach (var field in ex.Errors) foreach (var error in field.Value) Errors.Add($"{field.Key}: {error}");
                NameError = ex.Errors.TryGetValue("name", out var names) ? string.Join("\n", names) : "";
                GroupError = ex.Errors.TryGetValue(IsMaterials ? "category" : "department", out var groups) ? string.Join("\n", groups) : "";
                RateError = ex.Errors.TryGetValue(IsMaterials ? "defaultRatePlnKg" : "defaultRate", out var rates) ? string.Join("\n", rates) : "";
                Status = ex.Message + " Wpisane dane zachowano.";
            }
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { MarkUncertain("Wynik zapisu nieznany. Ponów ten sam zapis; dane i identyfikator zachowano."); return false; }
        finally { Busy = false; }
    }
    private void MarkUncertain(string message) { Uncertain = true; Status = message; }
    public async Task Compare(CrmClient api, CancellationToken ct)
    {
        if (!CanCompare || original is null) return;
        Busy = true;
        try
        {
            var row = IsMaterials
                ? CatalogRow.From(await api.ReadCatalog<CatalogMaterialDto>($"{Path}/{original.Id}", ct))
                : CatalogRow.From(await api.ReadCatalog<CatalogOperationDto>($"{Path}/{original.Id}", ct));
            ct.ThrowIfCancellationRequested();
            if (row.Id != original.Id || row.Version <= 0) throw new ApiFailure(502, "Nieprawidłowa pozycja do porównania.");
            compared = row;
            Comparison = $"Na serwerze (wersja {row.Version}):\n{row.Name}\n{GroupLabel}: {row.Group ?? "—"}\n{RateLabel}: {row.RateLabel}\n" +
                (IsMaterials ? $"{(row.IsActive ? "Aktywny" : "Archiwum")}\nUwagi: {row.Notes ?? "—"}" : $"Słowa do podpowiedzi: {row.Formula ?? "—"}") +
                $"\n\nTwój formularz:\n{Name}\n{GroupLabel}: {Group}\n{RateLabel}: {Rate}\n" +
                (IsMaterials ? $"{(Active ? "Aktywny" : "Archiwum")}\nUwagi: {Notes}" : $"Słowa do podpowiedzi: {Formula}");
            Status = "Porównaj dane. Akceptacja wersji zachowa Twój formularz przed ponownym zapisem.";
        }
        finally { Busy = false; }
    }
    public void AcceptComparison()
    {
        if (!CanAccept || compared is null) return;
        version = compared.Version; compared = null; pending = null; HasConflict = false; Uncertain = false;
        Comparison = ""; Status = "Używasz aktualnej wersji. Twój formularz zachowano; sprawdź dane i zapisz."; Notify();
    }
}
