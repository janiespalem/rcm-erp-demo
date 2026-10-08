using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum TemplateAction { Save, Upload, Apply, Archive, Restore }
public sealed partial class TemplateEditorViewModel : ObservableObject
{
    private sealed record DrawingUpload(Guid RequestId, long Version, string Filename, byte[] Bytes);
    private ProductTemplateDto? current;
    private ProductTemplateDto? compared;
    private readonly TemplateFeatures features;
    private readonly bool projectEditor;
    private string baseline = "";
    private object? pending;
    private TemplateAction pendingAction;
    private long pendingVersion;
    private DrawingUpload? drawing;
    private bool loading;
    private bool blocked;
    public ProductTemplateDto? Current => current;
    public double LaborRate => features.LaborRatePln;
    public bool Committed { get; private set; }
    public bool IsNew => current is null;
    public bool FormDirty => Snapshot() != baseline;
    public bool Dirty => FormDirty || HasSelectedDrawing || Uncertain;
    private bool Allowed => features.Write && Role == "technolog" && !blocked;
    public bool CanEdit => Allowed && !Busy && !Uncertain && !HasSelectedDrawing && current?.IsActive != false;
    public bool CanEditPrice => CanEdit && !projectEditor;
    public bool ReadOnly => !CanEdit;
    public bool ReadOnlyPrice => !CanEditPrice;
    public bool CanSave => CanEdit && !HasConflict && (IsNew || FormDirty);
    public bool CanRetry => Allowed && !Busy && Uncertain && pending is not null;
    public bool CanFiles => Allowed && current is { IsActive: true } && !Busy && !Uncertain && !HasConflict && !FormDirty;
    public bool CanChooseDrawing => CanFiles && features.Drawings && !HasSelectedDrawing;
    public bool CanUpload => CanFiles && drawing is not null;
    public bool CanPreview => CanFiles && features.Extract && current!.HasDrawing && !HasSelectedDrawing;
    public bool CanApply => CanPreview && Preview is not null;
    public bool CanLifecycle => CanFiles && !HasSelectedDrawing || Allowed && current is { IsActive: false } && !Busy && !Uncertain && !HasConflict && !Dirty;
    public bool CanDownloadDrawing => !Busy && features.Drawings && current is { HasDrawing: true } && Role is "biuro" or "technolog" or "ceo";
    public bool CanDownloadSheet => !Busy && features.Documents && current is not null && Role == "technolog";
    public bool CanDiscardDrawing => drawing is not null && !Busy && !Uncertain;
    public bool HasSelectedDrawing => drawing is not null;
    public bool HasPreview => Preview is not null;
    public bool CanCompare => HasConflict && !Busy && current is not null;
    public bool CanAccept => compared is not null && !Busy;
    public string Heading => current is null ? "Nowy szablon SOP" : $"Szablon · {current.Name}";
    public string LifecycleLabel => current?.IsActive == false ? "Przywróć szablon" : "Archiwizuj szablon";
    public string AccessHint => !features.Write ? "Katalog tylko do odczytu." : Role != "technolog" ? "Podgląd katalogu — tylko do odczytu." : current?.IsActive == false ? "Szablon w archiwum. Przywróć go przed edycją." : "Zapisz zmiany formularza przed pracą z rysunkiem.";
    public string PreviewSummary => Preview is not { } item ? "" : $"{item.Name}\nRysunek: {item.DrawingNumber ?? "—"} · Strony: {item.Pages}\nMateriał: {item.Material ?? "—"} · Masa: {item.MassKg?.ToString("N3") ?? "—"} kg\nProfil: {item.Profile ?? "—"}";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string category = "remont";
    [ObservableProperty] private string projectCode = "";
    [ObservableProperty] private string positionNumber = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string basePrice = "";
    [ObservableProperty] private string margin = "25";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comparison = "";
    [ObservableProperty] private string nameError = "";
    [ObservableProperty] private string? role;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private string drawingPath = "";
    [ObservableProperty] private TemplateDrawingPreview? preview;
    public ObservableCollection<TemplateOperationRow> Operations { get; } = [];
    public ObservableCollection<TemplateMaterialRow> Materials { get; } = [];
    public ObservableCollection<TemplateInstructionRow> Instructions { get; } = [];
    public ObservableCollection<TemplateMachineRow> Machines { get; } = [];
    public ObservableCollection<TemplateMaterialRow> PreviewMaterials { get; } = [];
    public ObservableCollection<TemplateOperationRow> PreviewOperations { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public static string[] SopLibrary { get; } = [
        "Oczyścić powierzchnię z rdzy i zanieczyszczeń", "Ocenić zakres uszkodzeń i udokumentować",
        "Pobrać materiał wg. listy materiałowej", "Wyciąć uszkodzone elementy (PIŁA / PLAZMA)",
        "Ciąć pręty na wymiar wg. rysunku", "Wykonać cięcie CNC wg. pliku DXF", "Giąć strzemiona wg. wymiarów (a, b, c)",
        "Giąć elementy na giętarce CNC", "Wspawać nowy element wg. pozycji fabrycznej (MIG)", "Wspawać wstawki — pełny przetop",
        "Zeszlifować spoiny do gładkości (SM)", "Montować kosz zbrojeniowy na stole montażowym", "Wiązać węzły drutem wiązałkowym co 200mm",
        "Zmontować elementy śrubowe i osie", "Sprawdzić wymiary pierwszej sztuki kontrolnej", "Sprawdzić wymiary liniowe wg. rysunku",
        "Sprawdzić wymiary geometryczne", "Sprawdzenie wymogów powierzchni", "Nałożyć powłokę antykorozyjną",
        "Skompletować elementy wg. listy zbiorczej", "Oznaczyć pojemnik numerem zlecenia"];
    public TemplateEditorViewModel(ProductTemplateDto? template, TemplateFeatures features, bool projectEditor = false)
    {
        this.features = features; this.projectEditor = projectEditor;
        Operations.CollectionChanged += RowsChanged; Materials.CollectionChanged += RowsChanged;
        Instructions.CollectionChanged += RowsChanged; Machines.CollectionChanged += RowsChanged;
        if (template is not null) LoadForm(template);
        baseline = Snapshot();
    }
    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (INotifyPropertyChanged row in e.OldItems) row.PropertyChanged -= RowChanged;
        if (e.NewItems is not null) foreach (INotifyPropertyChanged row in e.NewItems) row.PropertyChanged += RowChanged;
        Changed();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) => Changed();
    private void Changed() { if (!loading && !Busy && !Uncertain) pending = null; Notify(); }
    partial void OnNameChanged(string value) => Changed();
    partial void OnCategoryChanged(string value) => Changed();
    partial void OnProjectCodeChanged(string value) => Changed();
    partial void OnPositionNumberChanged(string value) => Changed();
    partial void OnNotesChanged(string value) => Changed();
    partial void OnBasePriceChanged(string value) => Changed();
    partial void OnMarginChanged(string value) => Changed();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    partial void OnHasConflictChanged(bool value) => Notify();
    partial void OnRoleChanged(string? value) => Notify();
    partial void OnPreviewChanged(TemplateDrawingPreview? value) { OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(PreviewSummary)); Notify(); }
    public void RefreshAccess(CrmClient api) => Role = api.Session?.Role;
    private void Notify()
    {
        foreach (var property in new[] { nameof(Current), nameof(IsNew), nameof(FormDirty), nameof(Dirty), nameof(CanEdit), nameof(CanEditPrice), nameof(ReadOnly), nameof(ReadOnlyPrice), nameof(CanSave), nameof(CanRetry), nameof(CanChooseDrawing), nameof(CanUpload), nameof(CanPreview), nameof(CanApply), nameof(CanLifecycle), nameof(CanDownloadDrawing), nameof(CanDownloadSheet), nameof(CanDiscardDrawing), nameof(HasSelectedDrawing), nameof(CanCompare), nameof(CanAccept), nameof(Heading), nameof(LifecycleLabel), nameof(AccessHint) }) OnPropertyChanged(property);
    }
    private string Snapshot() => JsonSerializer.Serialize(new { Name, Category, ProjectCode, PositionNumber, Notes, BasePrice, Margin,
        Operations = Operations.Select(row => row.Snapshot), Materials = Materials.Select(row => row.Snapshot), Instructions = Instructions.Select(row => row.Snapshot), Machines = Machines.Select(row => row.Snapshot) });
    private static IEnumerable<JsonNode?> Nodes(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())) : [];
    private void LoadForm(ProductTemplateDto template)
    {
        loading = true;
        try
        {
            current = template; Name = template.Name; Category = template.Category; ProjectCode = template.ProjectCode ?? "";
            PositionNumber = template.PositionNumber ?? ""; Notes = template.Notes ?? "";
            BasePrice = template.BasePricePln?.ToString(CultureInfo.CurrentCulture) ?? ""; Margin = (template.MarginPct * 100).ToString(CultureInfo.CurrentCulture);
            Operations.Clear(); foreach (var node in Nodes(template.Operations)) Operations.Add(new(node, features.LaborRatePln, true));
            Materials.Clear(); foreach (var node in Nodes(template.Materials)) Materials.Add(new(node, true));
            Instructions.Clear(); foreach (var node in Nodes(template.Instructions)) Instructions.Add(new(node, Instructions.Count + 1, true));
            Machines.Clear(); foreach (var node in Nodes(template.Machines)) Machines.Add(new(node, true));
            baseline = Snapshot();
        }
        finally { loading = false; Notify(); }
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private JsonElement ExportRows<T>(IEnumerable<T> rows, string label) where T : TemplateJsonRow
    {
        var array = new JsonArray(); var index = 0;
        foreach (var row in rows)
        {
            index++;
            try { array.Add(row.Export()); } catch (ArgumentException ex) { Errors.Add($"{label}, pozycja {index}: {ex.Message}"); }
        }
        if (index > 200) Errors.Add($"{label}: maksymalnie 200 pozycji.");
        return JsonSerializer.SerializeToElement(array);
    }
    private ProductTemplateDraft? Draft()
    {
        Errors.Clear(); NameError = Name.Trim().Length is < 1 or > 200 ? "Wpisz nazwę od 1 do 200 znaków." : "";
        if (Category.Trim().Length is < 1 or > 50) Errors.Add("Kategoria: wpisz od 1 do 50 znaków.");
        if (ProjectCode.Trim().Length > 50 || PositionNumber.Trim().Length > 50) Errors.Add("Projekt i pozycja: maksymalnie 50 znaków.");
        if (Notes.Length > 10000) Errors.Add("Uwagi: maksymalnie 10 000 znaków.");
        double? price = null;
        if (!string.IsNullOrWhiteSpace(BasePrice))
        {
            if (!OrderEditorViewModel.TryNumber(BasePrice, out var amount) || amount < 0 || amount > 99_999_999.99) Errors.Add("Cena bazowa: podaj liczbę od 0 do 99 999 999,99 lub pozostaw puste.");
            else price = amount;
        }
        if (projectEditor && current is { ProjectCode: not null }) price = current.BasePricePln;
        if (!OrderEditorViewModel.TryNumber(Margin, out var margin) || margin < 0 || margin > 100) Errors.Add("Marża: podaj procent od 0 do 100.");
        var draft = new ProductTemplateDraft(Name.Trim(), Category.Trim(), ExportRows(Operations, "Operacje"), ExportRows(Materials, "Materiały"),
            ExportRows(Instructions, "Instrukcje"), ExportRows(Machines, "Maszyny"), price, margin / 100, Empty(ProjectCode), Empty(PositionNumber), Empty(Notes));
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(draft)) > 60 * 1024) Errors.Add("Szablon jest zbyt duży. Zmniejsz liczbę lub długość pozycji.");
        return NameError.Length == 0 && Errors.Count == 0 ? draft : null;
    }
    public Task<bool> Save(CrmClient api, CancellationToken ct) => Mutate(api, TemplateAction.Save, ct);
    public Task<bool> Upload(CrmClient api, CancellationToken ct) => Mutate(api, TemplateAction.Upload, ct);
    public Task<bool> Apply(CrmClient api, CancellationToken ct) => Mutate(api, TemplateAction.Apply, ct);
    public Task<bool> Lifecycle(CrmClient api, CancellationToken ct) => Mutate(api, current?.IsActive == false ? TemplateAction.Restore : TemplateAction.Archive, ct);
    public Task<bool> Retry(CrmClient api, CancellationToken ct) => Mutate(api, pendingAction, ct);
    private async Task<bool> Mutate(CrmClient api, TemplateAction action, CancellationToken ct)
    {
        RefreshAccess(api);
        if (Uncertain ? !CanRetry : action switch { TemplateAction.Save => !CanSave, TemplateAction.Upload => !CanUpload, TemplateAction.Apply => !CanApply, _ => !CanLifecycle }) return false;
        if (pending is null)
        {
            pendingVersion = current?.Version ?? 0; pendingAction = action;
            if (action == TemplateAction.Save)
            {
                var draft = Draft(); if (draft is null) { Status = "Popraw pola. Wpisane dane zachowano."; return false; }
                pending = current is null ? new CreateProductTemplate(Guid.NewGuid(), draft) : new UpdateProductTemplate(Guid.NewGuid(), current.Version, draft);
            }
            else pending = action switch { TemplateAction.Upload => drawing!, TemplateAction.Apply => new ApplyTemplateDrawing(Guid.NewGuid(), pendingVersion), _ => new ProductTemplateVersionCommand(Guid.NewGuid(), pendingVersion) };
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            ProductTemplateDto result;
            if (pending is DrawingUpload upload)
                result = await api.UploadTemplateDrawing(current!.Id, upload.Version, upload.RequestId, upload.Filename, upload.Bytes, ct);
            else
            {
                var path = pendingAction switch
                {
                    TemplateAction.Save => current is null ? "" : $"/{current.Id}/update",
                    TemplateAction.Apply => $"/{current!.Id}/drawing/apply",
                    TemplateAction.Archive => $"/{current!.Id}/archive", _ => $"/{current!.Id}/restore"
                };
                result = await api.WriteTemplate<ProductTemplateDto>(path, pending, ct);
            }
            if (result.Id <= 0 || result.Version <= pendingVersion || current is not null && result.Id != current.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie zapisu szablonu.");
            pending = null; drawing = null; DrawingPath = ""; Uncertain = HasConflict = false; Preview = null; PreviewMaterials.Clear(); PreviewOperations.Clear();
            LoadForm(result); Committed = true; Status = "Zapis szablonu potwierdzony."; return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401) { Unknown("Zaloguj się ponownie i ponów tę samą operację. Dane zachowano."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500) { Unknown("Brak potwierdzenia. Ponów tę samą operację; dane i identyfikator zachowano."); return false; }
        catch (ApiFailure ex)
        {
            if (ex.Status == 409) { pending = null; Uncertain = false; HasConflict = true; compared = null; }
            else { pending = null; Uncertain = false; }
            blocked = ex.Status is 403 or 404 or 410;
            Errors.Clear(); foreach (var field in ex.Errors) foreach (var error in field.Value) Errors.Add($"{field.Key}: {error}");
            NameError = ex.Errors.TryGetValue("name", out var names) ? string.Join("\n", names) : "";
            Status = ex.Message + " Dane zachowano."; return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Unknown("Wynik operacji nieznany. Ponów tę samą operację; dane i identyfikator zachowano."); return false; }
        finally { Busy = false; }
    }
    private void Unknown(string message) { Uncertain = true; Status = message; }
    public async Task SelectDrawing(string path, CancellationToken ct)
    {
        if (!CanChooseDrawing) return;
        Busy = true;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            const int limit = 25 * 1024 * 1024;
            if (stream.Length > limit) throw new ApiFailure(413, "Rysunek przekracza limit 25 MiB.");
            using var bytes = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            { if (bytes.Length + read > limit) throw new ApiFailure(413, "Rysunek przekracza limit 25 MiB."); await bytes.WriteAsync(buffer.AsMemory(0, read), ct); }
            var snapshot = bytes.ToArray();
            if (snapshot.Length < 5 || !snapshot.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new ApiFailure(422, "Wybierz poprawny rysunek PDF.");
            drawing = new(Guid.NewGuid(), current!.Version, Path.GetFileName(path), snapshot); DrawingPath = path;
            Status = "Rysunek wybrany. Kliknij Prześlij PDF, aby zapisać go w szablonie.";
        }
        finally { Busy = false; }
    }
    public void DiscardDrawing() { if (CanDiscardDrawing) { drawing = null; DrawingPath = ""; pending = null; Notify(); } }
    public async Task PreviewDrawing(CrmClient api, CancellationToken ct)
    {
        RefreshAccess(api); if (!CanPreview) return; Busy = true; Status = "Odczytywanie rysunku…";
        try
        {
            var result = await api.WriteTemplate<TemplateDrawingPreview>($"/{current!.Id}/drawing/preview", null, ct); ct.ThrowIfCancellationRequested();
            PreviewMaterials.Clear(); foreach (var node in Nodes(result.Materials)) PreviewMaterials.Add(new(node));
            PreviewOperations.Clear(); foreach (var node in Nodes(result.Operations)) PreviewOperations.Add(new(node, features.LaborRatePln));
            Preview = result; Status = "Sprawdź odczyt. Zastosowanie zastąpi dane szablonu odczytem z PDF.";
        }
        finally { Busy = false; }
    }
    public async Task Download(CrmClient api, bool isDrawing, string destination, CancellationToken ct)
    {
        if (current is null || (isDrawing ? !CanDownloadDrawing : !CanDownloadSheet)) return;
        Busy = true; Status = "Pobieranie dokumentu…";
        try { await api.DownloadTemplate(current.Id, isDrawing, destination, ct); Status = $"Zapisano dokument: {destination}"; }
        finally { Busy = false; }
    }
    public async Task Compare(CrmClient api, CancellationToken ct)
    {
        if (!CanCompare || current is null) return; Busy = true;
        try
        {
            var result = await api.ReadTemplates<ProductTemplateDto>($"/{current.Id}", ct); ct.ThrowIfCancellationRequested();
            if (result.Id != current.Id || result.Version <= 0) throw new ApiFailure(502, "Nieprawidłowy szablon do porównania.");
            compared = result;
            Comparison = $"Na serwerze (wersja {result.Version}): {result.Name}\nKategoria: {result.Category} · Projekt: {result.ProjectCode ?? "—"} · Poz.: {result.PositionNumber ?? "—"}\nCena: {result.BasePricePln?.ToString("N2") ?? "—"} · Uwagi: {result.Notes ?? "—"}\nOperacje: {result.Operations.GetArrayLength()} · Materiały: {result.Materials.GetArrayLength()} · Instrukcje: {result.Instructions.GetArrayLength()}\n\nTwój formularz pozostaje w polach powyżej. Użycie wersji pozwoli zapisać Twój formularz zamiast danych na serwerze.";
        }
        finally { Busy = false; }
    }
    public void AcceptComparison()
    {
        if (!CanAccept || compared is null) return;
        current = compared; compared = null; pending = null; HasConflict = Uncertain = false;
        if (drawing is not null) drawing = drawing with { RequestId = Guid.NewGuid(), Version = current.Version };
        Preview = null; Comparison = ""; Status = "Aktualna wersja zaakceptowana. Twój formularz zachowano; sprawdź dane przed zapisem."; Notify();
    }
}
