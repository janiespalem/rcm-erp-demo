using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed record ProductionContractRow(ProductionContractDto Contract)
{
    public string Name => Contract.Fields.Name;
    public string Reference => Contract.Fields.Reference;
    public string Plan => Contract.Fields.PlannedTetrapods?.ToString() ?? "—";
    public string Deadline => Contract.Fields.Deadline?.ToString("dd.MM.yyyy") ?? "—";
    public string Mode => Contract.IsSynthetic ? "Testowy" : "Rzeczywisty";
}
public sealed record ProductionDeliveryRow(SteelDeliveryDto Delivery)
{
    public string Date => Delivery.Fields.DeliveryDate.ToString("dd.MM.yyyy");
    public string Diameter6 => ProductionMass.Display(Delivery.Fields.Steel.Diameter6);
    public string Diameter12 => ProductionMass.Display(Delivery.Fields.Steel.Diameter12);
    public string Diameter16 => ProductionMass.Display(Delivery.Fields.Steel.Diameter16);
    public string Note => Delivery.Fields.Note;
    public string Period => Delivery.BeforeOpening ? "Przed otwarciem — poza sumą" : "Według daty otwarcia";
}
public sealed record ProductionAuditRow(ProductionChangeDto Change)
{
    public long ActorId => Change.ActorId;
    public string Reason => Change.Reason;
    public string RecordedAt => TimeZoneInfo.ConvertTime(Change.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("pl-PL"));
    public string Action => Change.Action switch
    {
        "contract_created" => "Utworzenie kontraktu",
        "contract_changed" => "Zmiana kontraktu",
        "delivery_created" => "Dodanie dostawy",
        "delivery_corrected" => "Korekta dostawy",
        _ => "Zmiana danych"
    };
    public string Changes
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Change.Changes); var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return "Szczegóły zmiany niedostępne.";
                var lines = new List<string>();
                if (root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object) Describe(fields, lines);
                else if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object) Describe(changes, lines);
                else Describe(root, lines);
                if (root.TryGetProperty("norm", out var norm) && norm.ValueKind == JsonValueKind.Object && norm.TryGetProperty("basketsPerTetrapod", out var baskets) && norm.TryGetProperty("gramsPerTetrapod", out var steel))
                    lines.Add($"Norma: {baskets.GetInt32()} kosze / wyrób; {Steel(steel)} / wyrób");
                if (root.TryGetProperty("isSynthetic", out var synthetic) && synthetic.ValueKind is JsonValueKind.True or JsonValueKind.False) lines.Add(synthetic.GetBoolean() ? "Kontrakt testowy" : "Kontrakt rzeczywisty");
                return lines.Count == 0 ? "Zapis danych kontraktu lub dostawy." : string.Join("\n", lines);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { return "Szczegóły zmiany niedostępne."; }
        }
    }
    private static void Describe(JsonElement fields, List<string> lines)
    {
        foreach (var property in fields.EnumerateObject())
        {
            var label = property.Name switch
            {
                "name" => "Nazwa", "reference" => "Oznaczenie", "plannedTetrapods" => "Plan — wyroby", "deadline" => "Termin", "openingDate" => "Data otwarcia", "openingSteel" => "Stal początkowa", "normConfirmed" => "Norma potwierdzona", "deliveryDate" => "Data dostawy", "steel" => "Stal w dostawie", "note" => "Uwagi", _ => null
            };
            if (label is null) continue; var value = property.Value;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("before", out var before) && value.TryGetProperty("after", out var after)) lines.Add($"{label}: {Value(property.Name, before)} → {Value(property.Name, after)}");
            else lines.Add($"{label}: {Value(property.Name, value)}");
        }
    }
    private static string Value(string field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "nieznane";
        if (field is "steel" or "openingSteel") return Steel(value);
        if (field == "normConfirmed") return value.GetBoolean() ? "tak" : "nie";
        if (field is "openingDate" or "deadline" or "deliveryDate") return DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return value.ValueKind switch { JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? "—" : value.GetString()!, JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.GetCultureInfo("pl-PL")), _ => "nieznane" };
    }
    private static string Steel(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return ProductionMass.Summary(new());
        long? Mass(string key) => value.TryGetProperty(key, out var mass) && mass.ValueKind == JsonValueKind.Number ? mass.GetInt64() : null;
        return ProductionMass.Summary(new(Mass("diameter6"), Mass("diameter12"), Mass("diameter16")));
    }
}

public sealed partial class ProductionViewModel(CrmClient api) : ObservableObject
{
    private int epoch, detailEpoch;
    public ProductionReportsViewModel Reports { get; } = new(api);
    public ProductionFeatures Features { get; private set; } = new(false, false, default);
    public ObservableCollection<ProductionContractRow> Rows { get; } = [];
    public ObservableCollection<ProductionDeliveryRow> Deliveries { get; } = [];
    public ObservableCollection<ProductionAuditRow> Audit { get; } = [];
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool detailBusy;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private int deliveryPage = 1;
    [ObservableProperty] private int deliveryTotal;
    [ObservableProperty] private int auditPage = 1;
    [ObservableProperty] private int auditTotal;
    [ObservableProperty] private ProductionContractRow? selected;
    [ObservableProperty] private ProductionDeliveryRow? selectedDelivery;
    [ObservableProperty] private ProductionContractDto? detail;
    public bool CanRead => Features.Read && api.Session?.Role is "biuro" or "technolog" or "ceo" or "produkcja";
    public bool CanCreate => CanRead && Features.Write && api.Session?.Role is "biuro" or "technolog" && !Busy && !DetailBusy;
    public bool CanOpen => CanRead && Detail is not null && !Busy && !DetailBusy;
    public bool CanAddDelivery => CanCreate && Detail is not null;
    public bool CanCorrectDelivery => CanAddDelivery && SelectedDelivery is not null;
    public bool CanPrevious => !Busy && Page > 1;
    public bool CanNext => !Busy && Page * 50 < Total;
    public bool CanPreviousDelivery => !DetailBusy && DeliveryPage > 1;
    public bool CanNextDelivery => !DetailBusy && DeliveryPage * 50 < DeliveryTotal;
    public bool CanPreviousAudit => !DetailBusy && AuditPage > 1;
    public bool CanNextAudit => !DetailBusy && AuditPage * 50 < AuditTotal;
    public string PageInfo => $"Strona {Page} · {Total} kontraktów";
    public string DeliveryPageInfo => $"Strona {DeliveryPage} · {DeliveryTotal} dostaw";
    public string AuditPageInfo => $"Strona {AuditPage} · {AuditTotal} zmian";
    public string AccessHint => !CanRead ? "Ten serwer lub konto nie udostępnia produkcji." : Features.Write && api.Session?.Role is "biuro" or "technolog" ? "Biuro i technolog zapisują kontrakty oraz dostawy stali." : "Kontrakty i dostawy — tylko do odczytu.";
    public string DetailHeading => Detail?.Fields.Name ?? "Wybierz kontrakt";
    public string DetailInfo => Detail is { } d ? $"{d.Fields.Reference} · Plan: {d.Fields.PlannedTetrapods?.ToString() ?? "nieznany"} · Termin: {d.Fields.Deadline?.ToString("dd.MM.yyyy") ?? "nieznany"} · Otwarcie: {d.Fields.OpeningDate?.ToString("dd.MM.yyyy") ?? "nieustalone"}" : "";
    public string Norm => Detail is { } d ? $"Norma: {d.Norm.BasketsPerTetrapod} kosze / wyrób · {ProductionMass.Summary(d.Norm.GramsPerTetrapod)} / wyrób" : "";
    public string Material => Detail is { } d ? "Materiał według wpisów, bez odliczenia zużycia: " + ProductionMass.Summary(d.Coverage.AccountedSteel) : "";
    public string Potential => Detail is { } d ? $"Możliwe wyroby: {d.Coverage.PotentialTetrapods?.ToString() ?? "nieznane"} · Możliwe kosze: {d.Coverage.PotentialBaskets?.ToString() ?? "nieznane"}" : "";
    public string PlanMissing => Detail is { } d ? "Braki materiału dla pełnego planu: " + ProductionMass.Summary(d.Coverage.MissingForPlan) : "";
    public string Warning => Detail is not { } d ? "" : (!d.Fields.NormConfirmed ? "Norma niepotwierdzona dla kontraktu. " : "") + (d.Coverage.AccountedSteel is { Diameter6: null } or { Diameter12: null } or { Diameter16: null } ? "Nieznane masy uniemożliwiają pełny rachunek. " : "") + "Potencjał nie potwierdza fizycznego wykonania koszy. Faktyczny rozchód nie jest jeszcze rejestrowany; dane nie oznaczają stanu magazynu.";
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnDetailBusyChanged(bool value) => Notify();
    partial void OnPageChanged(int value) => Notify();
    partial void OnTotalChanged(int value) => Notify();
    partial void OnDeliveryPageChanged(int value) => Notify();
    partial void OnDeliveryTotalChanged(int value) => Notify();
    partial void OnAuditPageChanged(int value) => Notify();
    partial void OnAuditTotalChanged(int value) => Notify();
    partial void OnSelectedChanged(ProductionContractRow? value) => Notify();
    partial void OnSelectedDeliveryChanged(ProductionDeliveryRow? value) => Notify();
    partial void OnDetailChanged(ProductionContractDto? value) => Notify();
    public void SetFeatures(ProductionFeatures features) { Features = features; if (!CanRead) { Rows.Clear(); Selected = null; ClearDetail(); } Notify(); }
    public void Notify() { foreach (var p in new[] { nameof(CanRead), nameof(CanCreate), nameof(CanOpen), nameof(CanAddDelivery), nameof(CanCorrectDelivery), nameof(CanPrevious), nameof(CanNext), nameof(CanPreviousDelivery), nameof(CanNextDelivery), nameof(CanPreviousAudit), nameof(CanNextAudit), nameof(PageInfo), nameof(DeliveryPageInfo), nameof(AuditPageInfo), nameof(AccessHint), nameof(DetailHeading), nameof(DetailInfo), nameof(Norm), nameof(Material), nameof(Potential), nameof(PlanMissing), nameof(Warning) }) OnPropertyChanged(p); }
    public async Task Load(CancellationToken ct)
    {
        if (!CanRead) return; var version = Interlocked.Increment(ref epoch); Busy = true; Status = "Wczytywanie kontraktów…";
        try
        {
            var result = await api.ReadProduction<Page<ProductionContractDto>>($"/contracts?q={Uri.EscapeDataString(Query.Trim())}&page={Page}&pageSize=50", ct); ct.ThrowIfCancellationRequested(); if (version != epoch) return;
            var id = Selected?.Contract.Id; Rows.Clear(); foreach (var row in result.Items) Rows.Add(new(row)); Selected = Rows.FirstOrDefault(r => r.Contract.Id == id);
            Page = result.PageNumber; Total = result.Total; Status = Rows.Count == 0 ? "Brak kontraktów dla tego wyszukiwania." : "Dane aktualne.";
            if (Selected is null) ClearDetail();
        }
        finally { if (version == epoch) Busy = false; }
    }
    private void ClearDetail() { Reports.SelectContract(null); Interlocked.Increment(ref detailEpoch); Detail = null; Deliveries.Clear(); Audit.Clear(); SelectedDelivery = null; DeliveryPage = AuditPage = 1; DeliveryTotal = AuditTotal = 0; DetailBusy = false; }
    public async Task Open(Guid id, CancellationToken ct)
    {
        if (!CanRead) return; if (Detail?.Id != id) { Detail = null; Deliveries.Clear(); Audit.Clear(); SelectedDelivery = null; DeliveryTotal = AuditTotal = 0; } var version = Interlocked.Increment(ref detailEpoch); DetailBusy = true;
        try
        {
            var value = await api.ReadProduction<ProductionContractDto>($"/contracts/{id}", ct); ct.ThrowIfCancellationRequested(); if (version != detailEpoch) return;
            if (value.Id != id || value.Version < 1) throw new ApiFailure(502, "Nieprawidłowe dane kontraktu.");
            Detail = value; Reports.SelectContract(id); DeliveryPage = AuditPage = 1; Audit.Clear(); AuditTotal = 0;
            var rows = await api.ReadProduction<Page<SteelDeliveryDto>>($"/contracts/{id}/deliveries?page=1&pageSize=50", ct); ct.ThrowIfCancellationRequested(); if (version != detailEpoch) return; SetDeliveries(rows);
        }
        finally { if (version == detailEpoch) DetailBusy = false; }
    }
    private void SetDeliveries(Page<SteelDeliveryDto> result) { Deliveries.Clear(); foreach (var row in result.Items) Deliveries.Add(new(row)); SelectedDelivery = null; DeliveryTotal = result.Total; DeliveryPage = result.PageNumber; }
    public async Task LoadDeliveries(CancellationToken ct)
    {
        if (!CanRead || Detail is null) return; var version = Interlocked.Increment(ref detailEpoch); var id = Detail.Id; DetailBusy = true;
        try { var rows = await api.ReadProduction<Page<SteelDeliveryDto>>($"/contracts/{id}/deliveries?page={DeliveryPage}&pageSize=50", ct); ct.ThrowIfCancellationRequested(); if (version == detailEpoch) SetDeliveries(rows); }
        finally { if (version == detailEpoch) DetailBusy = false; }
    }
    public async Task LoadAudit(CancellationToken ct)
    {
        if (!CanRead || Detail is null) return; var version = Interlocked.Increment(ref detailEpoch); DetailBusy = true;
        try { var rows = await api.ReadProduction<Page<ProductionChangeDto>>($"/contracts/{Detail.Id}/audit?page={AuditPage}&pageSize=50", ct); ct.ThrowIfCancellationRequested(); if (version != detailEpoch) return; Audit.Clear(); foreach (var row in rows.Items) Audit.Add(new(row)); AuditPage = rows.PageNumber; AuditTotal = rows.Total; }
        finally { if (version == detailEpoch) DetailBusy = false; }
    }
    public void CancelRead() { Reports.Cancel(); Interlocked.Increment(ref epoch); Interlocked.Increment(ref detailEpoch); Busy = DetailBusy = false; }
    public void Clear() { CancelRead(); Reports.Clear(); Rows.Clear(); Selected = null; ClearDetail(); Query = Status = ""; Page = 1; Total = 0; Features = new(false, false, default); Notify(); }
}
