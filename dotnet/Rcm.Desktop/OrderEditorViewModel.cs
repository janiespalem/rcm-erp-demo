using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderMaterialRow : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string quantity = "";
}

public sealed partial class OrderEditorViewModel : ObservableObject
{
    private readonly OrderDto? original;
    private readonly string initial;
    private object? pending;
    private OrderDto? compared;
    private long version;
    public bool Intake { get; }
    public OrderDto? Saved { get; private set; }
    public bool IsNew => original is null;
    public bool CanEdit => !Saving && !Uncertain;
    public bool CanSave => !Saving && !HasConflict && Saved is null;
    public bool CanAccept => compared is not null && !Saving;
    public string Title => IsNew ? Intake ? "Przyjęcie zlecenia" : "Nowe zlecenie" : $"Edytuj zlecenie {original!.OrderNumber}";
    public string SaveLabel => Intake ? "Przyjmij i skieruj" : "Zapisz zlecenie";
    public static string[] InternalFirms { get; } = ["Demo Construction", "Demo Logistics", "Demo Concrete", "FactoryFlow"];
    public bool Dirty => Snapshot() != initial || Uncertain;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave), nameof(CanAccept))] private bool saving;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool uncertain;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool hasConflict;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comparison = "";
    [ObservableProperty] private string client = "";
    [ObservableProperty] private DateTime? deadline;
    [ObservableProperty] private string number = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string quantity = "1";
    [ObservableProperty] private string orderType = "remont";
    [ObservableProperty] private string material = "";
    [ObservableProperty] private string weight = "";
    [ObservableProperty] private string estimatedValue = "0";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string purpose = "";
    [ObservableProperty] private string sopName = "";
    [ObservableProperty] private string drawingNumber = "";
    [ObservableProperty] private string dimensions = "";
    [ObservableProperty] private string deliveryAddress = "";
    [ObservableProperty] private string contact = "";
    [ObservableProperty] private bool internalOrder;
    [ObservableProperty] private bool defence;
    [ObservableProperty] private bool hasDrawing;
    [ObservableProperty] private bool requiresVisit;
    [ObservableProperty] private long? templateId;
    [ObservableProperty] private long? approvedMaterialId;
    [ObservableProperty] private string clientError = "";
    [ObservableProperty] private string deadlineError = "";
    [ObservableProperty] private string descriptionError = "";
    [ObservableProperty] private string quantityError = "";
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<OrderMaterialRow> Materials { get; } = [];
    public OrderEditorViewModel(OrderDto? order = null, bool intake = false)
    {
        if (intake && order is not null) throw new ArgumentException("Przyjęcie dotyczy nowego zlecenia.", nameof(intake));
        Intake = intake;
        original = order; version = order?.VersionId ?? 0;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).Date;
        Deadline = order is null ? today.AddDays(7) : order.Deadline?.ToDateTime(TimeOnly.MinValue);
        if (order is not null)
        {
            Client = order.Client; Number = order.OrderNumber ?? ""; Description = order.Description ?? "";
            Quantity = (order.Quantity ?? 1).ToString(); OrderType = order.OrderType ?? "remont";
            Material = order.Material ?? ""; Weight = order.WeightKg?.ToString(CultureInfo.CurrentCulture) ?? "";
            EstimatedValue = (order.EstimatedValue ?? 0).ToString(CultureInfo.CurrentCulture);
            Notes = order.Notes ?? ""; Purpose = order.Purpose ?? ""; SopName = order.SopName ?? "";
            DrawingNumber = order.DrawingNumber ?? ""; Dimensions = order.Dimensions ?? "";
            DeliveryAddress = order.DeliveryAddress ?? ""; Contact = order.Contact ?? "";
            InternalOrder = order.IsInternal; Defence = order.IsDefence; HasDrawing = order.HasDrawing;
            RequiresVisit = order.RequiresVisit; TemplateId = order.TemplateId; ApprovedMaterialId = order.ApprovedMaterialId;
            foreach (var item in order.MaterialsJson) Materials.Add(new() { Name = item.Name ?? "", Quantity = item.QtyKg.ToString(CultureInfo.CurrentCulture) });
        }
        initial = Snapshot();
    }
    public void SelectInternalFirm(string firm)
    {
        if (!IsNew || !CanEdit || !InternalFirms.Contains(firm, StringComparer.Ordinal)) return;
        if (InternalOrder && Client == firm) { Client = ""; InternalOrder = false; }
        else { Client = firm; InternalOrder = true; }
    }
    public void SelectCategory(string category)
    {
        if (!IsNew || !CanEdit || category is not ("remont" or "nowa_czesc" or "catalog" or "zbrojenie")) return;
        OrderType = category; Defence = category == "zbrojenie";
    }
    private string Snapshot() => JsonSerializer.Serialize(new
    {
        Client, Deadline, Number, Description, Quantity, OrderType, Material, Weight, EstimatedValue, Notes, Purpose,
        SopName, DrawingNumber, Dimensions, DeliveryAddress, Contact, InternalOrder, Defence, HasDrawing, RequiresVisit,
        TemplateId, ApprovedMaterialId, Materials = Materials.Select(m => new { m.Name, m.Quantity }).ToArray()
    });
    public static bool TryNumber(string value, out double result) => double.TryParse(value.Trim().Replace(',', '.'),
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    private double Amount(string value, string label, double max, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return 0;
        if (TryNumber(value, out var result) && result >= 0 && result <= max) return result;
        Errors.Add($"{label}: podaj liczbę od 0 do {max}."); return 0;
    }
    private OrderFields? Validate()
    {
        Errors.Clear(); ClientError = DeadlineError = DescriptionError = QuantityError = "";
        if (string.IsNullOrWhiteSpace(Client) || Client.Trim().Length > 200) ClientError = "Wpisz nazwę klienta (do 200 znaków).";
        if (Deadline is null) DeadlineError = "Wybierz termin.";
        if (IsNew && string.IsNullOrWhiteSpace(Description)) DescriptionError = "Opisz zlecenie.";
        if (!int.TryParse(Quantity, out var count) || count is < 1 or > 1_000_000) QuantityError = "Podaj pełną liczbę sztuk od 1 do 1000000.";
        var weight = Amount(Weight, "Masa", 9_999_999.999, true);
        var estimate = Amount(EstimatedValue, "Wartość szacunkowa", 99_999_999.99);
        var materials = IsNew ? Materials.Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new OrderMaterialDto(m.Name.Trim(), Amount(m.Quantity, "Ilość materiału", 9_999_999.999, true))).ToArray() : original!.MaterialsJson;
        if (Errors.Count != 0 || new[] { ClientError, DeadlineError, DescriptionError, QuantityError }.Any(s => s.Length != 0)) return null;
        return new(Client.Trim(), DateOnly.FromDateTime(Deadline!.Value), Empty(Number), ApprovedMaterialId,
            Empty(Material) ?? materials.FirstOrDefault()?.Name, materials, HasDrawing, OrderType, Empty(SopName), Empty(Purpose), Empty(Notes), estimate,
            Description.Trim(), RequiresVisit, TemplateId, count, Defence, InternalOrder,
            string.IsNullOrWhiteSpace(Weight) ? null : weight, Empty(DrawingNumber), Empty(Dimensions), Empty(DeliveryAddress), Empty(Contact));
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        if (!CanSave) return false;
        if (pending is null)
        {
            var fields = Validate();
            if (fields is null) { Status = "Popraw zaznaczone pola. Wpisane dane są zachowane."; return false; }
            pending = IsNew ? new CreateOrder(Guid.NewGuid(), fields) : new EditOrder(version, fields);
        }
        Saving = true; Status = "Zapisywanie…";
        try
        {
            OrderTriageResult? triage = null;
            OrderDto saved;
            if (Intake)
            {
                var result = await api.WriteOrder<OrderIntakeResult>(HttpMethod.Post, "/intake", pending, ct);
                saved = result.Order; triage = result.Triage;
                if (triage is null || triage.Branch is not ("standard" or "niestandard" or "odrzut") || saved is null ||
                    saved.TriageBranch != triage.Branch || saved.Status != (saved.IsInternal ? "in_production" : triage.Branch == "odrzut" ? "rejected" : triage.Branch))
                    throw new ApiFailure(502, "Odpowiedź nie potwierdza przyjęcia zlecenia.");
            }
            else saved = await api.WriteOrder<OrderDto>(IsNew ? HttpMethod.Post : HttpMethod.Put, IsNew ? "" : $"/{original!.Id}", pending, ct);
            if (saved.Id <= 0 || saved.VersionId <= 0 || original is not null && saved.Id != original.Id)
                throw new ApiFailure(502, "Odpowiedź nie potwierdza zapisu tego zlecenia.");
            Saved = saved;
            Uncertain = false;
            Status = triage is null ? "Zapisano zlecenie." : string.Join("\n", new[] { triage.Message }.Concat(triage.Warnings));
            return true;
        }
        catch (ApiFailure ex)
        {
            Status = ex.Message;
            if (ex.Status >= 500 || Intake && ex.Status == 404) MarkUncertain();
            else if (IsNew && Uncertain)
            {
                HasConflict = false;
                Status += " Pierwotna operacja pozostaje zachowana. Sprawdź zapisane zlecenia lub ponów potwierdzenie z pierwotnego konta.";
            }
            else if (ex.Status == 409) { HasConflict = true; Uncertain = false; pending = null; }
            else if (ex.Status != 401)
            {
                Uncertain = false; pending = null; Errors.Clear();
                foreach (var field in ex.Errors) foreach (var message in field.Value) Errors.Add($"{field.Key}: {message}");
            }
            if (ex.Status == 401) { Uncertain = true; throw; }
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { MarkUncertain(); return false; }
        finally { Saving = false; }
    }
    private void MarkUncertain() { Uncertain = true; Status = "Nie można potwierdzić zapisu. Wpisane dane zachowano. Ponów tę samą operację po odzyskaniu połączenia."; }
    public async Task Compare(CrmClient api, CancellationToken ct)
    {
        if (IsNew) { HasConflict = false; Status = "Sprawdź numer zlecenia i popraw dane przed ponowieniem."; return; }
        compared = await api.ReadOrders<OrderDto>($"/{original!.Id}", ct);
        Comparison = $"Zapisana wersja {compared.VersionId}:\nNumer: {compared.OrderNumber}\nKlient: {compared.Client}\nTermin: {compared.Deadline:dd.MM.yyyy}\n" +
            $"Opis: {compared.Description}\nIlość: {compared.Quantity}\nRodzaj: {OrderLabels.Type(compared.OrderType)}\nMateriał: {compared.Material}\nMateriał ze słownika: {compared.ApprovedMaterialId}\n" +
            $"Masa: {compared.WeightKg} kg\nWartość szacunkowa: {compared.EstimatedValue:N2} PLN\nKontakt: {compared.Contact}\nDostawa: {compared.DeliveryAddress}\n" +
            $"Numer rysunku: {compared.DrawingNumber}\nWymiary: {compared.Dimensions}\nSOP: {compared.SopName}\nPrzeznaczenie: {compared.Purpose}\n" +
            $"Rysunek: {(compared.HasDrawing ? "tak" : "nie")} · Wizyta: {(compared.RequiresVisit ? "tak" : "nie")} · MON: {(compared.IsDefence ? "tak" : "nie")}\nUwagi: {compared.Notes}";
        OnPropertyChanged(nameof(CanAccept));
    }
    public void AcceptComparison()
    {
        if (compared is null || Saving) return;
        version = compared.VersionId; compared = null; pending = null; HasConflict = false;
        OnPropertyChanged(nameof(CanAccept)); Status = "Wersja porównana. Sprawdź własne dane i zapisz ponownie.";
    }
}
