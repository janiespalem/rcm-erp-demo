using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed record ProductionReportRow(ProductionReportReviewState Value)
{
    public string Date => Value.Report.ReportDate.ToString("dd.MM.yyyy");
    public string Shift => Value.Report.Shift;
    public string Author => Value.Report.AuthorName;
    public string Contract => Value.Link?.ContractName ?? "Bez kontraktu";
    public string Status => StateName(Value.State);
    public static string StateName(string state) => state switch { "unassigned" => "Bez kontraktu", "draft" => "Szkic", "pending" => "Do sprawdzenia", "accepted" => "Przyjęty", "returned" => "Zwrócony do korekty", "deleted" => "Usunięty", _ => "Nieznany stan" };
}
public sealed record ProductionReportAuditRow(ProductionReportAuditDto Value)
{
    public string Time => TimeZoneInfo.ConvertTime(Value.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("pl-PL"));
    public long Actor => Value.ActorId;
    public string Action => Value.Action switch { "report_linked" => "Przypisanie kontraktu", "report_relinked" => "Zmiana kontraktu", "report_accepted" => "Przyjęcie raportu", "report_returned" => "Zwrot do korekty", _ => "Zmiana raportu" };
    public string Reason => Value.Reason;
    public string Changes
    {
        get
        {
            var json = Value.Changes;
            string Contract(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("contractName", out var name) ? name.GetString() ?? "—" : "Bez kontraktu";
            if (json.ValueKind != JsonValueKind.Object) return "";
            if (json.TryGetProperty("before", out var before) && json.TryGetProperty("after", out var after)) return $"Kontrakt: {Contract(before)} → {Contract(after)}";
            if (json.TryGetProperty("reportVersion", out var report) && json.TryGetProperty("linkVersion", out var link)) return $"Wersja raportu: {report}; wersja przypisania: {link}";
            return "";
        }
    }
}

public sealed partial class ProductionReportReviewModel(ShiftReportsEditorViewModel owner) : ObservableObject
{
    private CrmClient? api;
    private object? pending;
    private long? pendingActor;
    private string pendingAction = "";
    private ProductionReportReviewState? compared;
    private bool installing;
    private bool blocked;
    public event EventHandler? Changed;
    public ProductionReviewFeatures Features { get; private set; } = new(false, false, false);
    public ProductionReportReviewState? State { get; private set; }
    public ObservableCollection<ProductionContractRow> Contracts { get; } = [];
    public ObservableCollection<ProductionReportAuditRow> Audit { get; } = [];
    [ObservableProperty] private ProductionContractRow? selectedContract;
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string linkReason = "";
    [ObservableProperty] private string reviewReason = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool initialized;
    [ObservableProperty] private int contractPage = 1;
    [ObservableProperty] private int contractTotal;
    [ObservableProperty] private int auditPage = 1;
    [ObservableProperty] private int auditTotal;
    public bool Available => Features.Read;
    public bool Locked => Busy || Uncertain || api?.Session?.ProductionEnabled == true && !Initialized;
    public bool SelectionDirty => SelectedContract is { } selected && selected.Contract.Id != State?.Link?.ContractId;
    public bool Dirty => SelectionDirty || LinkReason.Length > 0 || ReviewReason.Length > 0 || Uncertain;
    public bool FinalizeReady => !Available || State?.Report.Version == owner.Current?.Version && State is not null;
    public bool CanChoose => Available && Features.Link && owner.CanProductionLinkSource && !Busy && !Uncertain && !blocked;
    public bool CanLink => CanChoose && owner.Current is not null && SelectedContract is not null && SelectionDirty && !HasConflict;
    public bool CanReview => Available && Features.Review && owner.SchemaKnown && !owner.Removed && !blocked && !Busy && !Uncertain && !HasConflict && !owner.Busy && !owner.Uncertain && !owner.HasConflict && !owner.FormDirty && !owner.Correction && !SelectionDirty && State is { State: "pending", Link: not null } && State.Report.Version == owner.Current?.Version;
    public bool CanRetry => !Busy && !owner.Busy && Uncertain && pending is not null && api?.Session?.UserId == pendingActor && (pendingAction == "link" ? Features.Link && owner.CanProductionLinkSource : Features.Review);
    public bool CanCompare => HasConflict && !Busy && owner.Current is not null;
    public bool CanAcceptComparison => compared is not null && !Busy;
    public bool CanRefresh => !Busy && !owner.Busy && !Uncertain;
    public bool CanRead => Available && owner.Current is not null && !Busy && !owner.Busy;
    public bool CanPreviousContract => CanChoose && ContractPage > 1;
    public bool CanNextContract => CanChoose && ContractPage * 50 < ContractTotal;
    public bool CanPreviousAudit => CanRead && AuditPage > 1;
    public bool CanNextAudit => CanRead && AuditPage * 50 < AuditTotal;
    public string ContractPageInfo => $"Strona {ContractPage} · {ContractTotal} kontraktów";
    public string AuditPageInfo => $"Strona {AuditPage} · {AuditTotal} zmian";
    public string CurrentContract => State?.Link?.ContractName ?? "Bez przypisanego kontraktu";
    public string StateLabel => State is null ? "" : State.Report.Version != owner.Current?.Version ? "Wersja raportu zmieniona — wczytaj aktualny stan sprawdzenia." : ProductionReportRow.StateName(State.State);
    public string ReviewInfo => State?.CurrentReview is { } review && State.Report.Version == owner.Current?.Version ? $"Sprawdzający ID: {review.ReviewerId} · {ProductionReportRow.StateName(review.Decision)} · {review.Reason}" : "";
    public string ComparisonInfo => compared is null ? "" : $"Na serwerze: raport v{compared.Report.Version}, przypisanie v{compared.Link?.Version ?? 0}; {compared.Link?.ContractName ?? "Bez kontraktu"}; {ProductionReportRow.StateName(compared.State)}. Porównaj formularz przed ponowieniem.";
    public string LinkHint => owner.IsNew ? "Najpierw utwórz szkic, następnie zapisz wybrany kontrakt." : State?.Link is null ? "Wybierz jeden kontrakt i zapisz przypisanie." : "Zmiana kontraktu wymaga powodu. Przyjęcie dotyczy konkretnej wersji raportu i przypisania.";
    partial void OnSelectedContractChanged(ProductionContractRow? value) => DraftChanged();
    partial void OnLinkReasonChanged(string value) => DraftChanged();
    partial void OnReviewReasonChanged(string value) => DraftChanged();
    partial void OnBusyChanged(bool value) => Signal();
    partial void OnUncertainChanged(bool value) => Signal();
    partial void OnHasConflictChanged(bool value) => Signal();
    partial void OnInitializedChanged(bool value) => Signal();
    partial void OnContractPageChanged(int value) => Notify();
    partial void OnContractTotalChanged(int value) => Notify();
    partial void OnAuditPageChanged(int value) => Notify();
    partial void OnAuditTotalChanged(int value) => Notify();
    private void DraftChanged() { if (!installing && !Uncertain && !Busy) pending = null; Signal(); }
    private void Signal() { Notify(); Changed?.Invoke(this, EventArgs.Empty); }
    public void Notify()
    {
        foreach (var name in new[] { nameof(Features), nameof(Available), nameof(CanRefresh), nameof(FinalizeReady), nameof(Locked), nameof(SelectionDirty), nameof(Dirty), nameof(CanChoose), nameof(CanLink), nameof(CanReview), nameof(CanRetry), nameof(CanCompare), nameof(CanAcceptComparison), nameof(CanRead), nameof(CurrentContract), nameof(StateLabel), nameof(ReviewInfo), nameof(ComparisonInfo), nameof(LinkHint), nameof(CanPreviousContract), nameof(CanNextContract), nameof(ContractPageInfo), nameof(CanPreviousAudit), nameof(CanNextAudit), nameof(AuditPageInfo) }) OnPropertyChanged(name);
    }
    public void RefreshAccess(CrmClient client) { api = client; if (client.Session?.ProductionEnabled != true) { Features = new(false, false, false); Initialized = true; } Signal(); }
    public async Task Initialize(CrmClient client, CancellationToken ct)
    {
        RefreshAccess(client); if (client.Session?.ProductionEnabled != true) return;
        Busy = true;
        try
        {
            try { Features = await client.ReadProduction<ProductionReviewFeatures>("/review/features", ct); }
            catch (ApiFailure ex) when (ex.Status == 404) { Features = new(false, false, false); }
            ct.ThrowIfCancellationRequested();
            if (Features.Read && owner.Current is not null) await Refresh(ct);
            if (Features.Link && owner.CanProductionLinkSource) await LoadContracts(ct);
            Initialized = true;
        }
        finally { Busy = false; Signal(); }
    }
    private void Install(ProductionReportReviewState value, bool retainSelection)
    {
        if (value.Report.Id != owner.Current?.Id || value.Report.Version < 1 || value.Link is { } link && (link.ReportId != value.Report.Id || link.Version < 1)) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie raportu.");
        var hadDraft = SelectionDirty; State = value;
        if (!retainSelection || !hadDraft)
        {
            installing = true;
            try { SelectedContract = value.Link is null ? null : Contracts.FirstOrDefault(row => row.Contract.Id == value.Link.ContractId); }
            finally { installing = false; }
        }
        Signal();
    }
    public async Task Refresh(CancellationToken ct)
    {
        if (!Features.Read || owner.Current is null) return;
        var result = await api!.ReadProduction<ProductionReportReviewState>($"/reports/{owner.Current.Id}", ct); ct.ThrowIfCancellationRequested(); Install(result, true);
        if (result.Report.Version != owner.Current?.Version) { HasConflict = true; compared = null; Status = "Raport zmienił się. Pobierz wersję do porównania; wpisane dane zachowano."; }
    }
    public async Task LoadContracts(CancellationToken ct)
    {
        if (!Features.Link || !owner.CanProductionLinkSource || Uncertain) return;
        var result = await api!.ReadProduction<Page<ProductionContractDto>>($"/contracts?q={Uri.EscapeDataString(Query.Trim())}&page={ContractPage}&pageSize=50", ct); ct.ThrowIfCancellationRequested();
        var selected = SelectedContract; installing = true;
        try
        {
            Contracts.Clear(); foreach (var contract in result.Items) Contracts.Add(new(contract));
            if (selected is not null && Contracts.All(row => row.Contract.Id != selected.Contract.Id)) Contracts.Insert(0, selected);
            SelectedContract = selected is null ? Contracts.FirstOrDefault(row => row.Contract.Id == State?.Link?.ContractId) : Contracts.First(row => row.Contract.Id == selected.Contract.Id);
        }
        finally { installing = false; }
        ContractPage = result.PageNumber; ContractTotal = result.Total; Signal();
    }
    public Task<bool> Link(CancellationToken ct) => Mutate("link", "", ct);
    public Task<bool> Review(string decision, CancellationToken ct) => Mutate("review", decision, ct);
    public Task<bool> Retry(CancellationToken ct) => Mutate(pendingAction, "", ct);
    private async Task<bool> Mutate(string action, string decision, CancellationToken ct)
    {
        if (api is null || (Uncertain ? !CanRetry : action == "link" ? !CanLink : !CanReview)) return false;
        if (pending is null)
        {
            if (LinkReason.Trim().Length > 2000 || ReviewReason.Trim().Length > 2000) { Status = "Powód może mieć do 2000 znaków. Wpisane dane zachowano."; return false; }
            if (action == "link" && State?.Link is not null && string.IsNullOrWhiteSpace(LinkReason)) { Status = "Podaj powód zmiany kontraktu."; return false; }
            if (action == "review" && (decision is not ("accepted" or "returned") || decision == "returned" && string.IsNullOrWhiteSpace(ReviewReason))) { Status = "Podaj powód zwrotu raportu."; return false; }
            pendingAction = action; pendingActor = api.Session?.UserId;
            pending = action == "link" ? new LinkProductionReport(Guid.NewGuid(), owner.Current!.Version, State?.Link?.Version ?? 0, SelectedContract!.Contract.Id, LinkReason.Trim()) : new ReviewProductionReport(Guid.NewGuid(), owner.Current!.Version, State!.Link!.Version, decision, ReviewReason.Trim());
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            var result = await api.SaveProduction<ProductionReportReviewState>(HttpMethod.Post, $"/reports/{owner.Current!.Id}/{pendingAction}", pending, ct);
            if (result.Report.Version != owner.Current.Version || pending is LinkProductionReport link && (result.Link?.ContractId != link.ContractId || result.Link.Version != link.ExpectedLinkVersion + 1 || result.Link.SourceVersionAtLink != link.ExpectedReportVersion) || pending is ReviewProductionReport review && (result.State != review.Decision || result.Link?.Version != review.ExpectedLinkVersion || result.CurrentReview?.Decision != review.Decision || result.CurrentReview.ReportId != owner.Current.Id || result.CurrentReview.ContractId != result.Link.ContractId || result.CurrentReview.ReportVersion != review.ExpectedReportVersion || result.CurrentReview.LinkVersion != review.ExpectedLinkVersion)) throw new ApiFailure(502, "Brak poprawnego potwierdzenia operacji.");
            Install(result, false); pending = null; Uncertain = HasConflict = false; compared = null;
            installing = true; try { if (pendingAction == "link") LinkReason = ""; else ReviewReason = ""; } finally { installing = false; }
            owner.MarkProductionCommitted(); Status = "Zapis potwierdzony."; return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401) { Unknown("Sesja wygasła. Zaloguj się jako ta sama osoba i ponów tę samą operację."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500) { Unknown("Brak potwierdzenia. Ponów tę samą operację; wybór i powód zachowano."); return false; }
        catch (ApiFailure ex) { pending = null; Uncertain = false; HasConflict = ex.Status == 409; compared = null; blocked = ex.Status is 403 or 404 or 410; Status = ex.Message + " Wybór i powód zachowano."; return false; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Unknown("Wynik nieznany. Ponów tę samą operację; identyfikator zachowano."); return false; }
        finally { Busy = false; }
    }
    private void Unknown(string message) { Uncertain = true; Status = message; }
    public async Task Compare(CancellationToken ct)
    {
        if (!CanCompare) return; Busy = true;
        try
        {
            var value = await api!.ReadProduction<ProductionReportReviewState>($"/reports/{owner.Current!.Id}", ct); ct.ThrowIfCancellationRequested();
            if (value.Report.Version < 1 || value.Link is { } link && (link.ReportId != value.Report.Id || link.Version < 1)) throw new ApiFailure(502, "Nieprawidłowe dane porównania.");
            owner.CompareProductionSource(value.Report); compared = value; Notify();
        }
        finally { Busy = false; }
    }
    public void AcceptComparison()
    {
        if (!CanAcceptComparison || compared is null) return;
        owner.AcceptProductionSource(compared.Report); Install(compared, true); compared = null; HasConflict = false; pending = null;
        Status = "Aktualna wersja przyjęta do porównania. Wybór i powód zachowano; sprawdź dane przed nową operacją."; Signal();
    }
    public async Task ReadAudit(CancellationToken ct)
    {
        if (!CanRead) return; Busy = true;
        try { var page = await api!.ReadProduction<Page<ProductionReportAuditDto>>($"/reports/{owner.Current!.Id}/audit?page={AuditPage}&pageSize=50", ct); ct.ThrowIfCancellationRequested(); Audit.Clear(); foreach (var item in page.Items) Audit.Add(new(item)); AuditPage = page.PageNumber; AuditTotal = page.Total; }
        finally { Busy = false; }
    }
}
