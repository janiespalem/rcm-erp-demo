using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public static class ProductionMass
{
    public static bool TryGrams(string input, out long? grams)
    {
        grams = null; var value = input.Trim(); if (value.Length == 0) return true;
        value = value.Replace(',', '.'); var parts = value.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || parts.Any(p => p.Any(c => c is < '0' or > '9')) || parts.Length == 2 && (parts[1].Length is 0 or > 3)) return false;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var kg) || kg > 1_000_000_000m) return false;
        grams = (long)(kg * 1000m); return true;
    }
    public static string Kilograms(long? grams) => grams is null ? "" : (grams.Value / 1000m).ToString("0.###", CultureInfo.GetCultureInfo("pl-PL"));
    public static string Display(long? grams) => grams is null ? "nieznana" : Kilograms(grams) + " kg";
    public static string Summary(SteelMasses steel) => $"Ø6: {Display(steel.Diameter6)} · Ø12: {Display(steel.Diameter12)} · Ø16: {Display(steel.Diameter16)}";
}

public sealed partial class ProductionInput(string key, string label, string value = "") : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private string value = value;
}

public sealed partial class ProductionEditorViewModel : ObservableObject
{
    private readonly ProductionFeatures features;
    private readonly bool deliveryMode;
    private ProductionContractDto? contract;
    private SteelDeliveryDto? delivery;
    private ProductionContractDto? comparedContract;
    private SteelDeliveryDto? comparedDelivery;
    private object? pending;
    private string pendingPath = "";
    private HttpMethod pendingMethod = HttpMethod.Post;
    private long? actor, pendingActor;
    private string? role;
    private string baseline = "";
    private bool loading, blocked;
    public ProductionContractDto? Contract => contract;
    public SteelDeliveryDto? Delivery => delivery;
    public bool IsNew => deliveryMode ? delivery is null : contract is null;
    public bool IsContract => !deliveryMode;
    public bool IsDelivery => deliveryMode;
    public bool CanSynthetic => IsContract && IsNew && CanEdit;
    public bool Committed { get; private set; }
    public bool Dirty => Snapshot() != baseline || Uncertain;
    public bool CanEdit => features.Write && role is "biuro" or "technolog" && !blocked && !Busy && !Uncertain;
    public bool CanSave => CanEdit && !HasConflict && Dirty;
    public bool CanRetry => features.Write && role is "biuro" or "technolog" && !blocked && !Busy && Uncertain && pending is not null && pendingActor == actor;
    public bool CanCompare => !Busy && HasConflict && contract is not null;
    public bool CanAcceptComparison => !Busy && comparedContract is not null;
    public string Heading => IsContract ? (IsNew ? "Nowy kontrakt produkcyjny" : "Kontrakt produkcyjny") : (IsNew ? "Nowa dostawa stali" : "Korekta dostawy stali");
    public string AccessHint => features.Write && role is "biuro" or "technolog" ? "Zapis jest jawny. Puste pole masy oznacza nieznaną masę; 0 oznacza potwierdzony brak." : "Tylko odczyt. Zapis dostępny dla biura i technologa.";
    public string NormHint => $"Norma: {(contract?.Norm.BasketsPerTetrapod ?? 4)} kosze / wyrób · {ProductionMass.Summary(contract?.Norm.GramsPerTetrapod ?? new(3768, 3268, 49056))} / wyrób. Potwierdź zgodność z kontraktem.";
    public ObservableCollection<ProductionInput> Fields { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<string> Comparison { get; } = [];
    [ObservableProperty] private DateTime? deadline;
    [ObservableProperty] private DateTime? openingDate;
    [ObservableProperty] private DateTime? deliveryDate;
    [ObservableProperty] private bool normConfirmed;
    [ObservableProperty] private bool isSynthetic;
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool hasConflict;
    public DateTime Today => features.Today.ToDateTime(TimeOnly.MinValue);
    public ProductionInput Field(string key) => Fields.Single(f => f.Key == key);
    public ProductionEditorViewModel(ProductionFeatures features, ProductionContractDto? contract = null, SteelDeliveryDto? delivery = null, bool deliveryMode = false)
    {
        this.features = features; this.contract = contract; this.delivery = delivery; this.deliveryMode = deliveryMode;
        if (deliveryMode && contract is null) throw new ArgumentException("Dostawa wymaga kontraktu.");
        if (!deliveryMode) { Fields.Add(new("name", "Nazwa kontraktu")); Fields.Add(new("reference", "Numer / oznaczenie (opcjonalne)")); Fields.Add(new("planned", "Plan — wyroby (opcjonalne)")); }
        Fields.Add(new("d6", "Ø6 — kg")); Fields.Add(new("d12", "Ø12 — kg")); Fields.Add(new("d16", "Ø16 — kg"));
        if (deliveryMode) Fields.Add(new("note", "Uwagi (opcjonalne)"));
        foreach (var f in Fields) f.PropertyChanged += (_, _) => Changed();
        Install(); baseline = Snapshot();
    }
    public void RefreshAccess(CrmClient api) { actor = api.Session?.UserId; role = api.Session?.Role; Notify(); }
    private void Changed() { if (!loading && !Busy && !Uncertain) pending = null; Notify(); }
    partial void OnDeadlineChanged(DateTime? value) => Changed();
    partial void OnOpeningDateChanged(DateTime? value) => Changed();
    partial void OnDeliveryDateChanged(DateTime? value) => Changed();
    partial void OnNormConfirmedChanged(bool value) => Changed();
    partial void OnIsSyntheticChanged(bool value) => Changed();
    partial void OnReasonChanged(string value) => Changed();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    partial void OnHasConflictChanged(bool value) => Notify();
    public void Notify() { foreach (var p in new[] { nameof(Dirty), nameof(CanEdit), nameof(CanSave), nameof(CanRetry), nameof(CanCompare), nameof(CanAcceptComparison), nameof(CanSynthetic), nameof(IsNew), nameof(Heading), nameof(AccessHint), nameof(NormHint), nameof(Contract), nameof(Delivery) }) OnPropertyChanged(p); }
    private string Snapshot() => JsonSerializer.Serialize(new { Values = Fields.Select(f => f.Value), Deadline, OpeningDate, DeliveryDate, NormConfirmed, IsSynthetic, Reason });
    private void Install()
    {
        loading = true;
        try
        {
            var steel = deliveryMode ? delivery?.Fields.Steel : contract?.Fields.OpeningSteel;
            Field("d6").Value = ProductionMass.Kilograms(steel?.Diameter6); Field("d12").Value = ProductionMass.Kilograms(steel?.Diameter12); Field("d16").Value = ProductionMass.Kilograms(steel?.Diameter16);
            if (deliveryMode) { DeliveryDate = delivery?.Fields.DeliveryDate.ToDateTime(TimeOnly.MinValue) ?? Today; Field("note").Value = delivery?.Fields.Note ?? ""; }
            else
            {
                Field("name").Value = contract?.Fields.Name ?? ""; Field("reference").Value = contract?.Fields.Reference ?? ""; Field("planned").Value = contract?.Fields.PlannedTetrapods?.ToString(CultureInfo.InvariantCulture) ?? "";
                Deadline = contract?.Fields.Deadline?.ToDateTime(TimeOnly.MinValue); OpeningDate = contract?.Fields.OpeningDate?.ToDateTime(TimeOnly.MinValue); NormConfirmed = contract?.Fields.NormConfirmed ?? false; IsSynthetic = contract?.IsSynthetic ?? false;
            }
            Reason = ""; Errors.Clear(); baseline = Snapshot();
        }
        finally { loading = false; Notify(); }
    }
    private SteelMasses? Masses()
    {
        var result = new long?[3]; var keys = new[] { "d6", "d12", "d16" };
        for (var i = 0; i < keys.Length; i++) if (!ProductionMass.TryGrams(Field(keys[i]).Value, out result[i])) Errors.Add(Field(keys[i]).Label + ": podaj masę od 0 do 1 000 000 000 kg, najwyżej 3 miejsca po przecinku.");
        return Errors.Count == 0 ? new(result[0], result[1], result[2]) : null;
    }
    public Task<bool> Retry(CrmClient api, CancellationToken ct) => Send(api, true, ct);
    public Task<bool> Save(CrmClient api, CancellationToken ct) => Send(api, false, ct);
    private async Task<bool> Send(CrmClient api, bool retry, CancellationToken ct)
    {
        RefreshAccess(api); if (retry ? !CanRetry : !CanSave) return false;
        if (!retry)
        {
            Errors.Clear(); var steel = Masses();
            if (Reason.Trim().Length > 2000) Errors.Add("Powód: najwyżej 2000 znaków.");
            var id = Guid.NewGuid();
            if (deliveryMode)
            {
                if (DeliveryDate is null || DateOnly.FromDateTime(DeliveryDate.Value) > features.Today) Errors.Add("Wybierz datę dostawy nie późniejszą niż dzisiaj według serwera.");
                if (Field("note").Value.Trim().Length > 8000) Errors.Add("Uwagi: najwyżej 8000 znaków.");
                if (steel is { } supplied && !(supplied.Diameter6 > 0 || supplied.Diameter12 > 0 || supplied.Diameter16 > 0)) Errors.Add("Podaj co najmniej jedną dodatnią masę dostawy.");
                if (delivery is not null && string.IsNullOrWhiteSpace(Reason)) Errors.Add("Podaj powód korekty.");
                if (Errors.Count > 0 || steel is null) { Status = "Popraw pola. Wpisane dane zachowano."; return false; }
                var fields = new SteelDeliveryFields(DateOnly.FromDateTime(DeliveryDate!.Value), steel, Field("note").Value.Trim());
                pending = delivery is null ? new CreateSteelDelivery(id, contract!.Version, fields) : new CorrectSteelDelivery(id, contract!.Version, delivery.Version, fields, Reason.Trim());
                pendingPath = $"/contracts/{contract!.Id}/deliveries" + (delivery is null ? "" : $"/{delivery.Id}"); pendingMethod = HttpMethod.Post;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Field("name").Value) || Field("name").Value.Trim().Length > 240) Errors.Add("Nazwa kontraktu: od 1 do 240 znaków.");
                if (Field("reference").Value.Trim().Length > 240) Errors.Add("Numer: najwyżej 240 znaków.");
                int? planned = null; var plan = Field("planned").Value.Trim();
                if (plan.Length > 0) { if (int.TryParse(plan, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 1_000_000) planned = number; else Errors.Add("Plan: podaj liczbę całkowitą od 0 do 1 000 000."); }
                if (OpeningDate is { } opening && DateOnly.FromDateTime(opening) > features.Today) Errors.Add("Data otwarcia nie może być późniejsza niż dzisiaj według serwera.");
                if (OpeningDate is null && steel is { } s && (s.Diameter6 is not null || s.Diameter12 is not null || s.Diameter16 is not null)) Errors.Add("Początkowe masy wymagają daty otwarcia.");
                if (Errors.Count > 0 || steel is null) { Status = "Popraw pola. Wpisane dane zachowano."; return false; }
                var fields = new ProductionContractFields(Field("name").Value.Trim(), Field("reference").Value.Trim(), planned, Deadline is { } d ? DateOnly.FromDateTime(d) : null, OpeningDate is { } o ? DateOnly.FromDateTime(o) : null, steel, NormConfirmed);
                pending = contract is null ? new CreateProductionContract(id, fields, IsSynthetic) : new SaveProductionContract(id, contract.Version, fields, Reason.Trim()); pendingPath = "/contracts" + (contract is null ? "" : $"/{contract.Id}"); pendingMethod = contract is null ? HttpMethod.Post : HttpMethod.Put;
            }
            pendingActor = actor;
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            if (deliveryMode)
            {
                var result = await api.SaveProduction<SteelDeliveryResult>(pendingMethod, pendingPath, pending!, ct);
                if (result.ContractVersion <= contract!.Version || result.Delivery.ContractId != contract.Id || result.Delivery.Id == Guid.Empty || result.Delivery.Version <= (delivery?.Version ?? 0) || delivery is not null && delivery.Id != result.Delivery.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie dostawy.");
                delivery = result.Delivery; contract = contract with { Version = result.ContractVersion };
            }
            else
            {
                var result = await api.SaveProduction<ProductionContractDto>(pendingMethod, pendingPath, pending!, ct);
                if (result.Id == Guid.Empty || result.Version < 1 || result.Version < (contract?.Version ?? 0) || contract is not null && result.Id != contract.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie kontraktu.");
                contract = result;
            }
            pending = null; Uncertain = HasConflict = false; Committed = true; Install(); Status = "Zapis potwierdzony."; return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401) { Uncertain = true; Status = "Sesja wygasła. Zaloguj się jako ta sama osoba i ponów tę samą operację. Dane zachowano."; throw; }
        catch (ApiFailure ex) when (ex.Status >= 500) { Uncertain = true; Status = "Wynik nieznany. Ponów tę samą operację. Dane i identyfikator zachowano."; return false; }
        catch (ApiFailure ex)
        {
            pending = null; Uncertain = false; HasConflict = ex.Status == 409; blocked = ex.Status is 403 or 404 or 410; comparedContract = null; Comparison.Clear();
            Errors.Clear(); foreach (var pair in ex.Errors) foreach (var message in pair.Value) Errors.Add(pair.Key + ": " + message);
            Status = ex.Message + " Wpisane dane zachowano."; return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Uncertain = true; Status = "Wynik nieznany. Ponów tę samą operację. Dane i identyfikator zachowano."; return false; }
        finally { Busy = false; }
    }
    public async Task Compare(CrmClient api, CancellationToken ct)
    {
        if (!CanCompare) return; comparedContract = null; comparedDelivery = null; Comparison.Clear(); Busy = true;
        try
        {
            var latest = await api.ReadProduction<ProductionContractDto>($"/contracts/{contract!.Id}", ct);
            if (latest.Id != contract.Id || latest.Version < contract.Version) throw new ApiFailure(502, "Nieprawidłowa wersja kontraktu.");
            SteelDeliveryDto? latestDelivery = null;
            if (deliveryMode && delivery is not null)
            {
                latestDelivery = await api.ReadProduction<SteelDeliveryDto>($"/contracts/{contract.Id}/deliveries/{delivery.Id}", ct);
                if (latestDelivery.Id != delivery.Id || latestDelivery.ContractId != contract.Id || latestDelivery.Version < delivery.Version)
                    throw new ApiFailure(502, "Nieprawidłowa wersja dostawy. Zachowano Twoje pola.");
            }
            ct.ThrowIfCancellationRequested(); comparedContract = latest; comparedDelivery = latestDelivery; Comparison.Clear();
            Comparison.Add($"Serwer — kontrakt v{latest.Version}: {latest.Fields.Name}");
            if (!deliveryMode)
            {
                Comparison.Add($"Serwer — numer: {latest.Fields.Reference}; plan: {latest.Fields.PlannedTetrapods}; termin: {latest.Fields.Deadline}; otwarcie: {latest.Fields.OpeningDate}; norma potwierdzona: {latest.Fields.NormConfirmed}");
                Comparison.Add("Serwer — masy początkowe: " + ProductionMass.Summary(latest.Fields.OpeningSteel ?? new()));
            }
            else if (latestDelivery is not null)
            {
                Comparison.Add($"Serwer — dostawa v{latestDelivery.Version}, data: {latestDelivery.Fields.DeliveryDate}"); Comparison.Add("Serwer — " + ProductionMass.Summary(latestDelivery.Fields.Steel)); Comparison.Add("Serwer — uwagi: " + latestDelivery.Fields.Note);
            }
            foreach (var field in Fields) Comparison.Add($"Twój formularz — {field.Label}: {field.Value}");
            Comparison.Add($"Twój formularz — termin: {Deadline:dd.MM.yyyy}; otwarcie: {OpeningDate:dd.MM.yyyy}; dostawa: {DeliveryDate:dd.MM.yyyy}; norma: {NormConfirmed}");
            Status = "Porównaj dane. Przyjęcie wersji zachowa Twój formularz; zapis wymaga osobnego kliknięcia.";
        }
        finally { Busy = false; Notify(); }
    }
    public void AcceptComparison()
    {
        if (!CanAcceptComparison) return; contract = comparedContract; if (deliveryMode && delivery is not null) delivery = comparedDelivery;
        comparedContract = null; comparedDelivery = null; pending = null; HasConflict = Uncertain = false; Comparison.Clear(); Status = "Wersję serwera przyjęto. Twoje pola zachowano do jawnego zapisu."; Notify();
    }
}
