using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum ShiftReportsAction { Create, Save, Finalize, Correct, Discard, Delete }
public sealed partial class ShiftReportsEditorViewModel : ObservableObject
{
    private readonly ShiftReportFeatures features;
    private ShiftReportDto? current;
    private ShiftReportDto? compared;
    private string baseline = "";
    private bool loading;
    private bool blocked;
    private object? pending;
    private ShiftReportsAction pendingAction;
    private long pendingVersion;
    private long? pendingActor;
    private long? actorId;
    public bool Committed { get; private set; }
    public bool Removed { get; private set; }
    public ShiftReportDto? Current => current;
    public bool IsNew => current is null;
    public bool SchemaKnown => features.Schemas.TryGetValue(current?.SchemaVersion ?? 1, out var schema) && schema.Count == 9 && (current is null || current.Fields.Checks is { Length: 9 });
    private bool Active => !blocked && !Removed && current?.DeletedAt is null;
    private bool Owns => Active && features.Write && Role is "produkcja" or "technolog" && (current is null || Role == "technolog" || current.AuthorId == actorId);
    public bool CanEdit => Owns && SchemaKnown && !Production.Locked && !Busy && !Uncertain && current is not null && (current.Status == "draft" || Correction);
    public bool ReadOnly => !CanEdit;
    public bool CanCreate => IsNew && Owns && SchemaKnown && !Production.Locked && !Busy && !Uncertain && !HasConflict;
    public bool CanSave => CanEdit && !Correction && !HasConflict && FormDirty;
    public bool CanFinalize => Production.FinalizeReady && !Production.HasConflict && !Production.Locked && !Production.Dirty && Owns && SchemaKnown && current is { Status: "draft" } && !Busy && !Uncertain && !HasConflict && !FormDirty;
    public bool CanStartCorrection => !Production.Locked && Owns && SchemaKnown && current is not null && current.Status != "draft" && !Correction && !Busy && !Uncertain && !HasConflict;
    public bool CanCorrect => CanEdit && Correction && !HasConflict;
    public bool CanDiscard => CanFinalize && current!.AuthorId == actorId && current.ValidationErrors.Count > 0;
    public bool CanAdminDelete => !Production.Locked && !Production.Dirty && Active && features.CanAdminDelete && Role is "technolog" or "ceo" && current is not null && !Busy && !Uncertain && !HasConflict && !FormDirty && !Correction;
    public bool CanRetry => !Busy && Uncertain && pending is not null && pendingActor == actorId && (pendingAction == ShiftReportsAction.Delete ? features.CanAdminDelete && Role is "technolog" or "ceo" : Owns);
    public bool CanCompare => current is not null && !Busy && HasConflict;
    public bool CanAccept => compared is not null && !Busy;
    public bool CanReadAudit => current is not null && !Busy;
    public bool FormDirty => Snapshot() != baseline;
    public bool Dirty => Production.Dirty || FormDirty || Correction || Uncertain || DeleteReason.Length > 0;
    public bool OperationBusy => Busy || Production.Busy;
    public bool OperationUncertain => Uncertain || Production.Uncertain;
    public bool CanProductionLinkSource => Owns && !Busy && !Uncertain && !HasConflict;
    public string FinalizeLabel => Production.State?.Link is not null ? "Przekaż do sprawdzenia" : "Zakończ raport";
    public ProductionReportReviewModel Production { get; }
    public bool CanPrint => SchemaKnown && current is not null && !Busy;
    public string Heading => current is null ? "Otwórz raport zmiany" : $"Raport {current.ReportDate:dd.MM.yyyy} · Zmiana {current.Shift}";
    public string Description => current is null ? "Wybierz datę i zmianę, aby utworzyć lub otworzyć raport." : $"{ShiftReportsListRow.StateName(current.Status)} · Autor: {current.AuthorName} · Wersja {current.Version} · Korekty: {current.CorrectionCount}";
    public string AccessHint => !SchemaKnown ? "Nieobsługiwana wersja formularza. Zaktualizuj aplikację." : current?.DeletedAt is not null ? "Usunięty raport — tylko do odczytu. Powód jest zapisany w historii zmian." : !features.Write && !features.CanAdminDelete ? "Raport tylko do odczytu." : !Owns && !CanAdminDelete ? "Edycja jest dostępna dla autora i technologa." : Correction ? "Korekta pozostaje w formularzu do zatwierdzenia z podaniem powodu." : "Zapisz szkic przed zakończeniem. Ilości w podsumowaniu pochodzą z zakończonych raportów.";
    [ObservableProperty] private DateTime? reportDate;
    [ObservableProperty] private string shift = "I";
    [ObservableProperty] private string? role;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool correction;
    [ObservableProperty] private string correctionReason = "";
    [ObservableProperty] private string deleteReason = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string reasonError = "";
    [ObservableProperty] private bool includeDrafts;
    public ObservableCollection<ShiftReportsField> HeaderFields { get; } = [new("leader", "Kierownik zmiany"), new("responsible", "Odpowiedzialny"), new("people", "Ilość osób", 4, true), new("reference", "Zlecenie / partia", 200)];
    public ObservableCollection<ShiftReportsField> Quantities { get; } = [new("assembled", "Form złożonych", 7, true), new("prepared", "Form przygotowanych do zalania", 7, true), new("poured", "Gwiazdobloków zalanych na zmianie", 7, true), new("checked", "Form sprawdzonych po ok. 20 minutach", 7, true), new("demoulded", "Prefabrykatów wstępnie rozebranych", 7, true), new("damaged", "Uszkodzone — szt.", 7, true)];
    public ObservableCollection<ShiftReportsField> Notes { get; } = [new("damageReason", "Powód uszkodzenia", 2000, multiline: true), new("correctedWork", "Co poprawiono", 4000, multiline: true), new("remainingWork", "Co zostało do wykonania", 4000, multiline: true), new("workOwner", "Osoba odpowiedzialna", 100), new("remarks", "Uwagi ogólne", 4000, multiline: true)];
    public ObservableCollection<ShiftReportsField> Signatures { get; } = [new("productionPerson", "Osoba odpowiedzialna za produkcję"), new("controller", "Kierownik / osoba kontrolująca")];
    public ObservableCollection<ShiftReportsEquipment> Equipment { get; } = [new("vibrators", "Wibratory"), new("extensions", "Przedłużacze")];
    public ObservableCollection<ShiftReportsCheck> Checks { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<ShiftReportsChange> Comparison { get; } = [];
    public ObservableCollection<ShiftReportsAuditRow> Audit { get; } = [];
    public IEnumerable<ShiftReportsField> Fields => HeaderFields.Concat(Quantities).Concat(Notes).Concat(Signatures);
    public ShiftReportsField Field(string key) => Fields.Single(field => field.Key == key);
    public ShiftReportsEditorViewModel(ShiftReportDto? report, ShiftReportFeatures features, DateTime? date = null, string shift = "I")
    {
        Production = new(this); Production.Changed += (_, _) => Notify();
        this.features = features; current = report; ReportDate = report?.ReportDate.ToDateTime(TimeOnly.MinValue) ?? date ?? DateTime.Today; Shift = report?.Shift ?? shift;
        foreach (var row in Fields.Cast<INotifyPropertyChanged>().Concat(Equipment)) row.PropertyChanged += RowChanged;
        if (report is not null) Install(report); else { SetSchema(1); baseline = Snapshot(); }
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is not ("Error" or null)) Changed(); }
    private void SetSchema(int version)
    {
        foreach (var row in Checks) row.PropertyChanged -= RowChanged;
        Checks.Clear(); if (!features.Schemas.TryGetValue(version, out var schema)) return;
        for (var i = 0; i < schema.Count; i++) { var row = new ShiftReportsCheck(i, schema[i]); row.PropertyChanged += RowChanged; Checks.Add(row); }
    }
    private void Changed() { if (!loading && !Busy && !Uncertain) pending = null; Notify(); }
    partial void OnReportDateChanged(DateTime? value) => Changed();
    partial void OnShiftChanged(string value) => Changed();
    partial void OnCorrectionReasonChanged(string value) => Changed();
    partial void OnDeleteReasonChanged(string value) => Changed();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    partial void OnHasConflictChanged(bool value) => Notify();
    partial void OnCorrectionChanged(bool value) => Notify();
    partial void OnRoleChanged(string? value) => Notify();
    public void RefreshAccess(CrmClient api) { actorId = api.Session?.UserId; Role = api.Session?.Role; Production.RefreshAccess(api); Notify(); }
    private void Notify()
    {
        foreach (var key in new[] { nameof(Current), nameof(IsNew), nameof(SchemaKnown), nameof(CanEdit), nameof(ReadOnly), nameof(CanCreate), nameof(CanSave), nameof(CanFinalize), nameof(CanStartCorrection), nameof(CanCorrect), nameof(CanDiscard), nameof(CanAdminDelete), nameof(CanRetry), nameof(CanCompare), nameof(CanAccept), nameof(CanReadAudit), nameof(FormDirty), nameof(Dirty), nameof(CanPrint), nameof(Heading), nameof(Description), nameof(AccessHint), nameof(OperationBusy), nameof(OperationUncertain), nameof(FinalizeLabel) }) OnPropertyChanged(key);
        Production.Notify();
    }
    public void MarkProductionCommitted() => Committed = true;
    public void CompareProductionSource(ShiftReportDto report)
    {
        if (report.Id != current?.Id || report.SchemaVersion != current.SchemaVersion) throw new ApiFailure(409, "Wersja formularza zmieniła się. Zachowano Twoje pola.");
        Comparison.Clear(); var fields = Export();
        if (fields is not null) foreach (var change in ShiftReportsDifferences.Compare(JsonSerializer.SerializeToElement(report.Fields), JsonSerializer.SerializeToElement(fields), Labels())) Comparison.Add(change);
    }
    public void AcceptProductionSource(ShiftReportDto report)
    {
        if (report.Id != current?.Id || report.SchemaVersion != current.SchemaVersion) throw new ApiFailure(409, "Wersja formularza zmieniła się. Zachowano Twoje pola.");
        if (!FormDirty && !Correction) Install(report);
        else { compared = report; AcceptComparison(); }
    }
    private string Snapshot() => JsonSerializer.Serialize(new { ReportDate, Shift, Fields = Fields.Select(row => row.Value), Equipment = Equipment.Select(row => new { row.Condition, row.Reason, row.Note }), Checks = Checks.Select(row => row.Answer) });
    private void ClearErrors() { Errors.Clear(); ReasonError = ""; foreach (var row in Fields) row.Error = ""; foreach (var row in Equipment) row.Error = ""; foreach (var row in Checks) row.Error = ""; }
    private static string NormalizeKey(string key) => key.Replace("_", "").Replace("fields.", "").ToLowerInvariant();
    private void Error(string key, string message)
    {
        var normalized = NormalizeKey(key); var field = Fields.FirstOrDefault(row => NormalizeKey(row.Key) == normalized);
        if (field is not null) field.Error = message;
        else if (normalized.StartsWith("checks.") && int.TryParse(normalized[7..], out var index) && index >= 0 && index < Checks.Count) Checks[index].Error = message;
        else if (Equipment.FirstOrDefault(row => normalized.StartsWith(row.Key + ".")) is { } equipment) equipment.Error += message + " ";
        else if (normalized is "reason" or "correctionreason") ReasonError = message;
        Errors.Add(message);
    }
    private void Install(ShiftReportDto report)
    {
        loading = true;
        try
        {
            current = report; ReportDate = report.ReportDate.ToDateTime(TimeOnly.MinValue); Shift = report.Shift; SetSchema(report.SchemaVersion);
            var json = JsonSerializer.SerializeToElement(report.Fields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var row in Fields) row.Value = json.GetProperty(row.Key).ValueKind == JsonValueKind.Null ? "" : json.GetProperty(row.Key).ToString();
            for (var i = 0; i < Equipment.Count; i++) { var value = i == 0 ? report.Fields.Vibrators : report.Fields.Extensions; Equipment[i].Condition = value.Condition ?? ""; Equipment[i].Reason = value.Reason; Equipment[i].Note = value.Note; }
            for (var i = 0; i < Checks.Count && i < report.Fields.Checks.Length; i++) Checks[i].Answer = report.Fields.Checks[i] ?? "";
            Correction = false; CorrectionReason = DeleteReason = ""; ClearErrors(); Comparison.Clear(); compared = null; baseline = Snapshot();
        }
        finally { loading = false; Notify(); }
    }
    private ShiftReportFields? Export()
    {
        ClearErrors(); int? Number(string key)
        {
            var row = Field(key); if (string.IsNullOrWhiteSpace(row.Value)) return null;
            var min = key == "people" ? 1 : 0; var max = key == "people" ? 1000 : 1_000_000;
            if (int.TryParse(row.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max) return value;
            Error(key, $"{row.Label}: podaj liczbę całkowitą od {min} do {max}."); return null;
        }
        foreach (var row in Fields.Where(row => !row.Number)) if (row.Value.Trim().Length > row.Limit) Error(row.Key, $"{row.Label}: maksymalnie {row.Limit} znaków.");
        foreach (var row in Equipment) if (row.Reason.Length > 1000 || row.Note.Length > 1000) Error(row.Key + ".reason", $"{row.Label}: maksymalnie 1000 znaków w polu.");
        var result = new ShiftReportFields { Leader = Field("leader").Value.Trim(), Responsible = Field("responsible").Value.Trim(), People = Number("people"), Reference = Field("reference").Value.Trim(),
            Assembled = Number("assembled"), Prepared = Number("prepared"), Poured = Number("poured"), Checked = Number("checked"), Demoulded = Number("demoulded"), Damaged = Number("damaged"),
            DamageReason = Field("damageReason").Value.Trim(), Vibrators = Equipment[0].Export(), Extensions = Equipment[1].Export(), Checks = Checks.Select(row => string.IsNullOrEmpty(row.Answer) ? null : row.Answer).ToArray(),
            CorrectedWork = Field("correctedWork").Value.Trim(), RemainingWork = Field("remainingWork").Value.Trim(), WorkOwner = Field("workOwner").Value.Trim(), Remarks = Field("remarks").Value.Trim(),
            ProductionPerson = Field("productionPerson").Value.Trim(), Controller = Field("controller").Value.Trim() };
        return Errors.Count == 0 ? result : null;
    }
    public void StartCorrection() { if (CanStartCorrection) { Correction = true; Status = "Uzupełnij korektę i podaj powód."; } }
    public void CancelCorrection() { if (Correction && !Busy && !Uncertain && current is { } report) Install(report); }
    public Task<bool> Create(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Create, ct);
    public Task<bool> Save(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Save, ct);
    public Task<bool> FinalizeReport(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Finalize, ct);
    public Task<bool> Correct(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Correct, ct);
    public Task<bool> Discard(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Discard, ct);
    public Task<bool> AdminDelete(CrmClient api, CancellationToken ct) => Mutate(api, ShiftReportsAction.Delete, ct);
    public Task<bool> Retry(CrmClient api, CancellationToken ct) => Mutate(api, pendingAction, ct);
    private async Task<bool> Mutate(CrmClient api, ShiftReportsAction action, CancellationToken ct)
    {
        RefreshAccess(api);
        if (Uncertain ? !CanRetry : action switch { ShiftReportsAction.Create => !CanCreate, ShiftReportsAction.Save => !CanSave, ShiftReportsAction.Finalize => !CanFinalize, ShiftReportsAction.Correct => !CanCorrect, ShiftReportsAction.Discard => !CanDiscard, _ => !CanAdminDelete }) return false;
        if (pending is null)
        {
            ClearErrors(); pendingAction = action; pendingVersion = current?.Version ?? 0; pendingActor = actorId;
            var id = Guid.NewGuid();
            if (action == ShiftReportsAction.Create)
            { if (ReportDate is null || Shift is not ("I" or "II")) { Status = "Wybierz datę i zmianę I lub II."; return false; } pending = new CreateShiftReport(id, DateOnly.FromDateTime(ReportDate.Value), Shift); }
            else if (action is ShiftReportsAction.Save or ShiftReportsAction.Correct)
            {
                var fields = Export(); if (fields is null) { Status = "Popraw pola. Wpisane wartości zachowano."; return false; }
                if (action == ShiftReportsAction.Correct && (string.IsNullOrWhiteSpace(CorrectionReason) || CorrectionReason.Trim().Length > 2000)) { Error("reason", "Podaj powód korekty (do 2000 znaków)."); return false; }
                pending = action == ShiftReportsAction.Save ? new SaveShiftReport(id, pendingVersion, fields) : new CorrectShiftReport(id, pendingVersion, fields, CorrectionReason.Trim());
            }
            else if (action == ShiftReportsAction.Delete)
            { if (string.IsNullOrWhiteSpace(DeleteReason) || DeleteReason.Trim().Length > 2000) { Error("reason", "Podaj powód usunięcia (do 2000 znaków)."); return false; } pending = new AdminDeleteShiftReport(id, pendingVersion, DeleteReason.Trim()); }
            else pending = new ShiftReportVersionCommand(id, pendingVersion);
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            var suffix = pendingAction switch { ShiftReportsAction.Create => "", ShiftReportsAction.Save => "/save", ShiftReportsAction.Finalize => "/finalize", ShiftReportsAction.Correct => "/corrections", ShiftReportsAction.Discard => "/discard", _ => "/admin-delete" };
            var path = pendingAction == ShiftReportsAction.Create ? "" : $"/{current!.Id}{suffix}";
            if (pendingAction is ShiftReportsAction.Discard or ShiftReportsAction.Delete)
            {
                var result = await api.WriteShiftReport<ShiftReportMutationResult>(path, pending, ct);
                if (result.Id != current!.Id || result.Version <= pendingVersion || !result.Deleted) throw new ApiFailure(502, "Brak poprawnego potwierdzenia usunięcia.");
                Removed = true; Correction = false; DeleteReason = CorrectionReason = ""; baseline = Snapshot();
            }
            else
            {
                var result = await api.WriteShiftReport<ShiftReportDto>(path, pending, ct);
                if (result.Id <= 0 || result.Version <= pendingVersion || current is not null && result.Id != current.Id) throw new ApiFailure(502, "Brak poprawnego potwierdzenia raportu.");
                if (pending is CreateShiftReport create && (result.ReportDate != create.ReportDate || result.Shift != create.Shift)) throw new ApiFailure(502, "Potwierdzenie dotyczy innej zmiany.");
                Install(result);
            }
            pending = null; Uncertain = HasConflict = false; Committed = true; Status = Removed ? "Usunięcie potwierdzone." : "Zapis potwierdzony."; return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401) { Unknown("Sesja wygasła. Zaloguj się jako ta sama osoba i ponów operację. Formularz zachowano."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500) { Unknown("Brak potwierdzenia. Ponów tę samą operację; formularz zachowano."); return false; }
        catch (ApiFailure ex)
        {
            pending = null; Uncertain = false; HasConflict = ex.Status == 409; compared = null; blocked = ex.Status is 403 or 404 or 410;
            ClearErrors(); foreach (var field in ex.Errors) Error(field.Key, string.Join("\n", field.Value)); Status = ex.Message + " Formularz zachowano."; return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Unknown("Wynik nieznany. Ponów tę samą operację; formularz i identyfikator zachowano."); return false; }
        finally { Busy = false; }
    }
    private void Unknown(string message) { Uncertain = true; Status = message; }
    private IReadOnlyDictionary<string, string> Labels()
    {
        Dictionary<string, string> labels = Fields.ToDictionary(row => NormalizeKey(row.Key), row => row.Label);
        foreach (var row in Checks) labels[$"checks.{row.Index}"] = row.Label;
        foreach (var row in Equipment) foreach (var (key, label) in new[] { ("condition", "Stan"), ("reason", "Powód / osoba"), ("note", "Uwaga / nr") }) labels[$"{row.Key}.{key}"] = row.Label + " · " + label;
        return labels;
    }
    public async Task Compare(CrmClient api, CancellationToken ct)
    {
        if (!CanCompare) return; Busy = true;
        try
        {
            var report = await api.ReadShiftReports<ShiftReportDto>($"/{current!.Id}", ct); ct.ThrowIfCancellationRequested();
            if (report.Id != current.Id || report.Version <= 0 || report.SchemaVersion != current.SchemaVersion) throw new ApiFailure(409, "Wersja formularza zmieniła się. Zachowano Twoje pola.");
            var fields = Export(); if (fields is null) return;
            compared = report; Comparison.Clear();
            foreach (var change in ShiftReportsDifferences.Compare(JsonSerializer.SerializeToElement(report.Fields), JsonSerializer.SerializeToElement(fields), Labels())) Comparison.Add(change);
            Status = $"Na serwerze: wersja {report.Version} · {ShiftReportsListRow.StateName(report.Status)}. Porównaj pola i zaakceptuj wersję przed zapisem.";
        }
        finally { Busy = false; }
    }
    public void AcceptComparison()
    {
        if (!CanAccept || compared is null) return;
        var fieldValues = Fields.Select(row => row.Value).ToArray();
        var equipmentValues = Equipment.Select(row => (row.Condition, row.Reason, row.Note)).ToArray();
        var checkValues = Checks.Select(row => row.Answer).ToArray();
        var correctionText = CorrectionReason; var deletionText = DeleteReason;
        Install(compared); compared = null; HasConflict = Uncertain = false; pending = null;
        loading = true;
        try
        {
            var index = 0; foreach (var row in Fields) row.Value = fieldValues[index++];
            for (index = 0; index < Equipment.Count; index++) { Equipment[index].Condition = equipmentValues[index].Condition; Equipment[index].Reason = equipmentValues[index].Reason; Equipment[index].Note = equipmentValues[index].Note; }
            for (index = 0; index < Checks.Count; index++) Checks[index].Answer = checkValues[index];
            CorrectionReason = correctionText; DeleteReason = deletionText;
            if (current!.Status != "draft" && Owns) Correction = true;
        }
        finally { loading = false; }
        Comparison.Clear(); Status = "Aktualna wersja zaakceptowana. Twoje wartości zachowano do jawnego zapisu."; Notify();
    }
    public async Task ReadAudit(CrmClient api, CancellationToken ct)
    {
        if (!CanReadAudit) return; Busy = true;
        try
        {
            var result = await api.ReadShiftReports<ShiftReportAuditDto[]>($"/{current!.Id}/audit?includeDrafts={IncludeDrafts.ToString().ToLowerInvariant()}", ct); ct.ThrowIfCancellationRequested();
            Audit.Clear(); foreach (var entry in result) Audit.Add(new(entry, ShiftReportsDifferences.Compare(entry.Before, entry.After, Labels())));
            Status = Audit.Count == 0 ? "Brak zdarzeń dla wybranego filtra." : "Historia zmian aktualna.";
        }
        finally { Busy = false; }
    }
}
