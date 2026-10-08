using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class QuoteProcessRow : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string department = "";
    [ObservableProperty] private string material = "";
    [ObservableProperty] private string hours = "0";
    [ObservableProperty] private string rate = "0";
    [ObservableProperty] private string cost = "0";
}
public sealed partial class QuoteMaterialRow : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string quantity = "0";
    [ObservableProperty] private string price = "0";
    [ObservableProperty] private string cost = "0";
}
public sealed partial class OrderQuoteViewModel(OrderDto order) : ObservableObject
{
    private string? baseline;
    public OrderDto Order { get; } = order;
    public bool Saved { get; private set; }
    public bool Dirty => baseline is not null && baseline != Snapshot() || Uncertain;
    public bool CanEdit => Loaded && !Busy;
    public bool CanSave => CanEdit && !Uncertain;
    public bool CanLoad => !Loaded && !Busy;
    public bool IsManual => Method == "reczna";
    public bool IsStructured => !IsManual;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave), nameof(CanLoad))] private bool loaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave), nameof(CanLoad), nameof(CanAcceptVerified))] private bool busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool uncertain;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsManual), nameof(IsStructured))] private string method = "kalkulacja";
    [ObservableProperty] private string weightBasis = "netto";
    [ObservableProperty] private string laborHours = "0";
    [ObservableProperty] private string overhead = order.IsInternal ? "0" : "10";
    [ObservableProperty] private string margin = order.IsInternal ? "0" : "25";
    [ObservableProperty] private string transport = "0";
    [ObservableProperty] private string weightNet = "0";
    [ObservableProperty] private string weightGross = "0";
    [ObservableProperty] private string weightRate = "0";
    [ObservableProperty] private string materialWeight = "0";
    [ObservableProperty] private string materialPrice = "0";
    [ObservableProperty] private string materialCost = "0";
    [ObservableProperty] private string manualTotal = "0";
    [ObservableProperty] private bool showUnitPrices = !order.IsInternal;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string result = "";
    public ObservableCollection<QuoteProcessRow> Processes { get; } = [];
    public ObservableCollection<QuoteMaterialRow> Materials { get; } = [];
    private static string F(double? value) => (value ?? 0).ToString(CultureInfo.CurrentCulture);
    private string Snapshot() => JsonSerializer.Serialize(new { Method, WeightBasis, LaborHours, Overhead, Margin, Transport, WeightNet, WeightGross, WeightRate, MaterialWeight, MaterialPrice, MaterialCost, ManualTotal, ShowUnitPrices,
        Processes = Processes.Select(p => new { p.Name, p.Department, p.Material, p.Hours, p.Rate, p.Cost }), Materials = Materials.Select(m => new { m.Name, m.Quantity, m.Price, m.Cost }) });
    public async Task Load(CrmClient api, CancellationToken ct)
    {
        Busy = true;
        try
        {
            OrderQuoteDto? quote = null;
            try { quote = await api.ReadOrders<OrderQuoteDto>($"/{Order.Id}/quote", ct); }
            catch (ApiFailure ex) when (ex.Status == 404) { }
            ct.ThrowIfCancellationRequested();
            if (quote is not null)
            {
                Method = quote.PricingMethod ?? "kalkulacja"; WeightBasis = quote.WeightBasis ?? "netto";
                LaborHours = F(quote.LaborHours); Overhead = F((quote.OverheadPct ?? .10) * 100); Margin = F((quote.MarginPct ?? .25) * 100);
                Transport = F(quote.TransportCost); WeightNet = F(quote.WeightNettoKg); WeightGross = F(quote.WeightBruttoKg);
                WeightRate = F(quote.WeightRatePlnKg); MaterialWeight = F(quote.MaterialWeightKg); MaterialPrice = F(quote.MaterialPricePerKg);
                MaterialCost = F(quote.MaterialCost); ManualTotal = F(quote.TotalNet); ShowUnitPrices = quote.ShowUnitPrices;
                if (quote.WeightNettoKg is null or 0 && WeightBasis == "netto") WeightNet = F(quote.WeightKg);
                if (quote.WeightBruttoKg is null or 0 && WeightBasis == "brutto") WeightGross = F(quote.WeightKg);
                Processes.Clear(); Materials.Clear();
                foreach (var p in quote.ProcessesJson ?? []) Processes.Add(new() { Name = p.Name, Department = p.Department ?? "", Material = p.Material ?? "", Hours = p.Hours is null ? "" : F(p.Hours), Rate = p.RatePerHour is null ? "" : F(p.RatePerHour), Cost = F(p.Cost) });
                foreach (var m in quote.MaterialsJson ?? []) Materials.Add(new() { Name = m.Name ?? m.Material ?? "", Quantity = F(m.QtyKg), Price = F(m.PricePerKg), Cost = F(m.Cost) });
                Result = $"Zapisana wycena: {quote.TotalNet:N2} PLN netto";
            }
            else
            {
                WeightNet = F(Order.WeightKg);
                foreach (var material in Order.MaterialsJson) Materials.Add(new() { Name = material.Name ?? "", Quantity = F(material.QtyKg) });
            }
            Loaded = true; Uncertain = false; baseline = Snapshot(); Status = "Dane wyceny wczytane.";
        }
        finally { Busy = false; }
    }
    private static double Number(string text, string field, double max)
    {
        if (!OrderEditorViewModel.TryNumber(text, out var value) || value < 0 || value > max)
            throw new ArgumentException($"{field}: podaj liczbę od 0 do {max}.");
        return value;
    }
    private OrderQuoteInput Input()
    {
        var materials = Materials.Select(m => new OrderQuoteMaterial(m.Name, null, Number(m.Quantity, "Ilość materiału", 9_999_999.999), Number(m.Price, "Cena materiału", 1_000_000), Number(m.Cost, "Koszt materiału", 99_999_999.99))).ToArray();
        var net = Number(WeightNet, "Masa netto", 9_999_999.999);
        var gross = Number(WeightGross, "Masa brutto", 9_999_999.999);
        if (gross == 0) gross = materials.Sum(m => m.QtyKg);
        return new()
        {
        Method = Method, WeightBasis = WeightBasis, ShowUnitPrices = ShowUnitPrices,
        Processes = Processes.Select(p => new OrderQuoteProcess(string.IsNullOrWhiteSpace(p.Name) ? throw new ArgumentException("Wpisz nazwę każdej operacji.") : p.Name.Trim(), p.Department, p.Material,
            string.IsNullOrWhiteSpace(p.Hours) ? null : Number(p.Hours, "Godziny operacji", 1_000_000), string.IsNullOrWhiteSpace(p.Rate) ? null : Number(p.Rate, "Stawka operacji", 1_000_000), Number(p.Cost, "Koszt operacji", 99_999_999.99))).ToArray(),
        Materials = materials,
        LaborHours = Number(LaborHours, "Dodatkowa robocizna", 1_000_000), OverheadPct = Number(Overhead, "Koszty ogólne (%)", 100) / 100,
        MarginPct = Number(Margin, "Marża (%)", 100) / 100, TransportCost = Number(Transport, "Transport", 99_999_999.99),
        WeightKg = Method == "od_masy" ? (WeightBasis == "brutto" ? gross : net) : 0, WeightNettoKg = net, WeightBruttoKg = gross,
        WeightRatePlnKg = Number(WeightRate, "Stawka za kg", 1_000_000), MaterialWeightKg = materials.Length > 0 ? materials.Sum(m => m.QtyKg) : Number(MaterialWeight, "Masa materiału", 9_999_999.999),
        MaterialPricePerKg = Number(MaterialPrice, "Cena za kg materiału", 1_000_000), MaterialCost = Number(MaterialCost, "Koszt materiału", 99_999_999.99)
        };
    }
    public async Task Preview(CrmClient api, CancellationToken ct)
    {
        if (!CanEdit) return;
        try
        {
            if (IsManual) { Result = $"Cena ręczna: {Number(ManualTotal, "Cena netto", 99_999_999.99):N2} PLN netto"; return; }
            var input = Input(); Busy = true;
            var result = await api.WriteOrder<OrderQuotePreview>(HttpMethod.Post, "/quote/preview", input, ct);
            Result = $"{result.TotalNet:N2} PLN netto\nMateriały: {result.MaterialTotal:N2} · operacje: {result.OpsTotal:N2} · robocizna dodatkowa: {result.ExtraLabor:N2}";
            Status = "Obliczono podgląd. Wycena nie została jeszcze zapisana.";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
        finally { Busy = false; }
    }
    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        if (!CanSave) return false;
        try
        {
            object input = IsManual ? new { totalNet = Number(ManualTotal, "Cena netto", 99_999_999.99) } : Input();
            Busy = true;
            await api.WriteOrder<OrderQuoteDto>(HttpMethod.Post, $"/{Order.Id}/quote" + (IsManual ? "/manual" : ""), input, ct);
            Saved = true; baseline = Snapshot(); Status = "Zapisano wycenę."; return true;
        }
        catch (ArgumentException ex) { Status = ex.Message; return false; }
        catch (ApiFailure ex) when (ex.Status >= 500) { MarkUncertain(); return false; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { MarkUncertain(); return false; }
        finally { Busy = false; }
    }
    private void MarkUncertain() { Uncertain = true; Status = "Wynik zapisu nieznany. Wczytaj zapisaną wycenę i porównaj ją z tym formularzem przed kolejnym zapisem."; }
    public async Task Verify(CrmClient api, CancellationToken ct)
    {
        OrderQuoteDto quote;
        try { quote = await api.ReadOrders<OrderQuoteDto>($"/{Order.Id}/quote", ct); }
        catch (ApiFailure ex) when (ex.Status == 404)
        {
            Comparison = "Na serwerze nie ma obecnie zapisanej wyceny tego zlecenia. Możesz odblokować zapis i ponowić go z zachowanymi wartościami.";
            verified = true; OnPropertyChanged(nameof(Comparison)); OnPropertyChanged(nameof(CanAcceptVerified)); return;
        }
        Comparison = $"Zapisana wycena: {quote.TotalNet:N2} PLN netto\nOstatnia zmiana: {quote.LastEditedAt:dd.MM.yyyy HH:mm}\n" +
            $"Metoda: {quote.PricingMethod}; robocizna: {quote.LaborHours} h; koszty ogólne: {quote.OverheadPct:P0}; marża: {quote.MarginPct:P0}; transport: {quote.TransportCost:N2} PLN\n" +
            $"Masa do wyceny: {quote.WeightKg} kg × {quote.WeightRatePlnKg} PLN/kg; podstawa: {quote.WeightBasis}\nMasa netto/brutto: {quote.WeightNettoKg}/{quote.WeightBruttoKg} kg\n" +
            $"Materiał bez pozycji: {quote.MaterialWeightKg} kg × {quote.MaterialPricePerKg} PLN/kg; koszt: {quote.MaterialCost:N2} PLN\nCeny jednostkowe: {(quote.ShowUnitPrices ? "tak" : "nie")}\n" +
            string.Join("\n", (quote.ProcessesJson ?? []).Select(p => $"{p.Name} ({p.Department}, {p.Material}): {p.Hours} h × {p.RatePerHour} PLN/h; koszt: {p.Cost:N2}")) + "\n" +
            string.Join("\n", (quote.MaterialsJson ?? []).Select(m => $"{m.Name ?? m.Material}: {m.QtyKg} kg × {m.PricePerKg} PLN/kg; koszt: {m.Cost:N2}"));
        OnPropertyChanged(nameof(Comparison));
        verified = true; OnPropertyChanged(nameof(CanAcceptVerified));
    }
    public string Comparison { get; private set; } = "";
    private bool verified;
    public bool CanAcceptVerified => verified && !Busy;
    public void AcceptVerified() { if (!CanAcceptVerified) return; Uncertain = false; verified = false; OnPropertyChanged(nameof(CanAcceptVerified)); Status = "Porównano zapis. Własne wartości pozostają w formularzu; zapis jest ponownie dostępny."; }
}
