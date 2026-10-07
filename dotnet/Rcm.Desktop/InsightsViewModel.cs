using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum InsightSection { Analytics, Production, Schedule, Profitability, Benchmark, ServiceHistory }
public sealed record InsightChoice(InsightSection Section, string Title);
public sealed record InsightColumn(string Key, string Title, double Width = 140);
public sealed record InsightMetric(string Title, string Value);
public sealed record InsightTableRow(IReadOnlyDictionary<string, string> Values)
{
    public string this[string key] => Values.GetValueOrDefault(key, "—");
}

public sealed partial class InsightsViewModel(CrmClient api) : ObservableObject
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private int readVersion;
    private InsightFeatures? features;
    [ObservableProperty] private InsightSection section;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string material = "";
    [ObservableProperty] private string orderType = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool exporting;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    public ObservableCollection<InsightChoice> Choices { get; } = [];
    public ObservableCollection<InsightColumn> Columns { get; } = [];
    public ObservableCollection<InsightTableRow> Rows { get; } = [];
    public ObservableCollection<InsightMetric> Metrics { get; } = [];
    public ObservableCollection<RevenueMonthInsight> Revenue { get; } = [];
    public ObservableCollection<TopClientInsight> Clients { get; } = [];
    public string Title => api.Session?.Role == "biuro" ? "Historia usług" : "Raporty zleceń";
    public bool AnalyticsVisible => Section == InsightSection.Analytics;
    public bool BenchmarkVisible => Section == InsightSection.Benchmark;
    public bool SearchVisible => !AnalyticsVisible && !BenchmarkVisible;
    public bool CanPrevious => !Busy && Page > 1 && !AnalyticsVisible;
    public bool CanNext => !Busy && Page * 50 < Total && !AnalyticsVisible;
    public bool ExportAllowed => features?.Export == true && api.Session?.Role is "technolog" or "ceo";
    public bool CanExport => features?.Export == true && api.Session?.Role is "technolog" or "ceo" && !Exporting;
    public string Scope => Section switch
    {
        InsightSection.Analytics => "Przychód: ostatnie 183 dni. Statystyki obejmują również archiwum. Lista poniżej: zaległe terminy (maks. 100).",
        InsightSection.Profitability => $"Ostatnie {features?.ProfitabilityWindow ?? 50} kwalifikujących się zleceń. Koszt pracy: godziny rzeczywiste, a przy ich braku — oszacowanie. „—” oznacza brak danych.",
        InsightSection.ServiceHistory => $"Przeszukiwanie ostatnich {features?.HistoryWindow ?? 500} wpisów preferowanego źródła historii usług.",
        InsightSection.Benchmark => "Porównanie ceny netto za kilogram. Statystyka wymaga co najmniej 3 porównywalnych próbek.",
        _ => "Zlecenia obejmują również archiwum. Wyszukiwanie traktuje wpisany tekst dosłownie."
    };
    partial void OnSectionChanged(InsightSection value)
    {
        CancelRead(); Page = 1; ClearResults();
        foreach (var name in new[] { nameof(AnalyticsVisible), nameof(BenchmarkVisible), nameof(SearchVisible), nameof(Scope), nameof(CanPrevious), nameof(CanNext) }) OnPropertyChanged(name);
    }
    partial void OnBusyChanged(bool value) => NotifyActions();
    partial void OnExportingChanged(bool value) => NotifyActions();
    partial void OnPageChanged(int value) => NotifyActions();
    partial void OnTotalChanged(int value) => NotifyActions();
    private void NotifyActions() { OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(ExportAllowed)); }
    public void CancelRead() { Interlocked.Increment(ref readVersion); Busy = false; }
    private void ClearResults() { Rows.Clear(); Columns.Clear(); Metrics.Clear(); Revenue.Clear(); Clients.Clear(); Total = 0; PageInfo = ""; }
    public void Clear() { CancelRead(); Section = InsightSection.Analytics; OnPropertyChanged(nameof(Title)); features = null; Choices.Clear(); ClearResults(); Search = Material = OrderType = Status = ""; Page = 1; NotifyActions(); }
    public async Task Activate(CancellationToken ct)
    {
        var version = Interlocked.Increment(ref readVersion);
        var role = api.Session?.Role;
        if (role is not ("technolog" or "ceo" or "biuro")) { Clear(); Status = "Brak dostępu do raportów."; return; }
        Busy = true;
        try
        {
            var received = await api.ReadInsights<InsightFeatures>("/features", ct);
            ct.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref readVersion)) return;
            features = received;
            Choices.Clear();
            void Add(InsightSection s, string title, bool allowed) { if (allowed && (role != "biuro" || s == InsightSection.ServiceHistory)) Choices.Add(new(s, title)); }
            Add(InsightSection.Analytics, "Analiza działalności", received.Analytics);
            Add(InsightSection.Production, "Produkcja", received.Production);
            Add(InsightSection.Schedule, "Harmonogram", received.Schedule);
            Add(InsightSection.Profitability, "Rentowność", received.Profitability);
            Add(InsightSection.Benchmark, "Porównanie cen", received.Benchmark);
            Add(InsightSection.ServiceHistory, "Historia usług", received.ServiceHistory);
            if (!Choices.Any(c => c.Section == Section) && Choices.FirstOrDefault() is { } first) Section = first.Section;
            OnPropertyChanged(nameof(Section)); NotifyActions(); OnPropertyChanged(nameof(Scope));
            if (Choices.Count == 0) { ClearResults(); Status = "Raporty nie są dostępne dla tego konta."; return; }
            await Load(ct);
        }
        finally { if (version == Volatile.Read(ref readVersion)) Busy = false; }
    }
    public async Task Load(CancellationToken ct)
    {
        if (!Choices.Any(c => c.Section == Section)) return;
        var version = Interlocked.Increment(ref readVersion);
        var section = Section; var page = Page;
        var query = $"?page={page}&pageSize=50&q={Uri.EscapeDataString(Search)}";
        Busy = true; Status = "Wczytywanie raportu…";
        // Build results privately: a superseded request must never clear newer data.
        var rows = new List<InsightTableRow>(); var columns = new List<InsightColumn>(); var metrics = new List<InsightMetric>();
        RevenueMonthInsight[] revenue = []; TopClientInsight[] clients = []; int total = 0; string? warning = null;
        void Col(string key, string title, double width = 140) => columns.Add(new(key, title, width));
        void Row(params (string Key, string? Value)[] values) => rows.Add(new(values.ToDictionary(v => v.Key, v => v.Value ?? "—")));
        void Metric(string title, string value) => metrics.Add(new(title, value));
        void Schedule(ScheduleInsight r) => Row(("number", r.OrderNumber ?? $"#{r.Id}"), ("client", r.Client), ("status", State(r.Status)), ("deadline", Date(r.Deadline)), ("branch", r.Branch));
        try
        {
            switch (section)
            {
                case InsightSection.Analytics:
                    var a = await api.ReadInsights<AnalyticsInsight>("/analytics", ct);
                    Metric("Wszystkie zlecenia", a.TotalOrders.ToString(Polish)); Metric("W produkcji / gotowe", $"{a.OrdersInProduction} / {a.OrdersDone}");
                    Metric("Odrzucone", $"{a.RejectedCount} · {Percent(a.RejectedPct)}"); Metric("Standardowe / indywidualne", $"{a.StandardCount} / {a.CustomCount}");
                    Metric("Średnia marża", Percent(a.AverageMarginPct)); Metric("Cykl realizacji", Number(a.AverageCycleDays) + " dni");
                    Metric("Oferta → start", Number(a.AverageQuoteToStartDays) + " dni"); Metric("Rzeczywiste / planowane", Percent(a.EstimateAccuracyPct));
                    Metric("Zaległe terminy", a.OverdueTotal.ToString(Polish)); revenue = a.RevenueByMonth; clients = a.TopClients;
                    foreach (var r in a.OverdueOrders) Schedule(r); total = a.OverdueTotal;
                    break;
                case InsightSection.Schedule:
                    var s = await api.ReadInsights<Page<ScheduleInsight>>("/schedule" + query, ct); total = s.Total; foreach (var r in s.Items) Schedule(r); break;
                case InsightSection.Production:
                    var p = await api.ReadInsights<Page<ProductionInsight>>("/production" + query, ct); total = p.Total;
                    foreach (var r in p.Items) Row(("number", r.OrderNumber ?? $"#{r.Id}"), ("client", r.Client), ("status", State(r.Status)), ("deadline", Date(r.Deadline)), ("description", r.Description), ("material", r.Material), ("routing", string.Join(" → ", r.Routing)), ("price", Money(r.TotalNet))); break;
                case InsightSection.Profitability:
                    var profits = await api.ReadInsights<Page<ProfitabilityInsight>>("/profitability" + query, ct); total = profits.Total;
                    foreach (var r in profits.Items) Row(("number", r.OrderNumber), ("client", r.Client), ("status", State(r.Status)), ("price", Money(r.PricePln)), ("material", Money(r.MaterialCostPln)), ("labor", Money(r.LaborCostPln)), ("cost", Money(r.CostPln)), ("margin", Money(r.MarginPln)), ("pct", Percent(r.MarginPct)), ("hours", Number(r.ActualHours))); break;
                case InsightSection.Benchmark:
                    var b = await api.ReadInsights<BenchmarkInsight>($"/benchmark?page={page}&pageSize=50&material={Uri.EscapeDataString(Material)}&orderType={Uri.EscapeDataString(OrderType)}", ct);
                    total = b.Samples.Total; warning = b.Warning; Metric("Próbek", b.Count.ToString(Polish)); Metric("Średnia zł/kg", Number(b.AveragePlnKg)); Metric("Minimum zł/kg", Number(b.MinimumPlnKg)); Metric("Maksimum zł/kg", Number(b.MaximumPlnKg));
                    foreach (var r in b.Samples.Items) Row(("number", $"#{r.OrderId}"), ("date", Date(r.Date)), ("weight", Number(r.WeightKg)), ("price", Money(r.TotalNet)), ("kg", Number(r.PlnKg))); break;
                case InsightSection.ServiceHistory:
                    var h = await api.ReadInsights<Page<ServiceHistoryInsight>>("/service-history" + query, ct); total = h.Total;
                    foreach (var r in h.Items) Row(("number", r.SourceOrderNumber ?? $"#{r.Id}"), ("date", Date(r.OrderDate)), ("client", r.Client), ("type", r.OrderType), ("description", r.Description), ("material", r.Material), ("materialCost", Money(r.MaterialCost)), ("constructor", Number(r.ConstructorHours)), ("production", Number(r.ProductionHours)), ("price", Money(r.TotalPrice)), ("source", r.Source)); break;
            }
            if (section is InsightSection.Analytics or InsightSection.Schedule or InsightSection.Production or InsightSection.Profitability) { Col("number", "Zlecenie", 120); Col("client", "Klient", 210); Col("status", "Status", 130); }
            if (section is InsightSection.Analytics or InsightSection.Schedule or InsightSection.Production) Col("deadline", "Termin", 110);
            if (section is InsightSection.Analytics or InsightSection.Schedule) Col("branch", "Dział");
            if (section == InsightSection.Production) { Col("description", "Opis", 240); Col("material", "Materiał"); Col("routing", "Przebieg operacji", 260); Col("price", "Cena netto"); }
            if (section == InsightSection.Profitability) { Col("price", "Cena netto"); Col("material", "Materiały"); Col("labor", "Praca"); Col("cost", "Koszt łącznie"); Col("margin", "Marża kwota"); Col("pct", "Marża %", 110); Col("hours", "Godziny rzeczywiste", 160); }
            if (section == InsightSection.Benchmark) { Col("number", "Zlecenie"); Col("date", "Data", 110); Col("weight", "Waga kg"); Col("price", "Cena netto"); Col("kg", "zł/kg"); }
            if (section == InsightSection.ServiceHistory) { Col("number", "Nr źródłowy"); Col("date", "Data", 110); Col("client", "Klient", 210); Col("type", "Typ"); Col("description", "Opis", 240); Col("material", "Materiał"); Col("materialCost", "Koszt materiału"); Col("constructor", "Konstruktor h"); Col("production", "Produkcja h"); Col("price", "Cena łącznie"); Col("source", "Źródło", 240); }
            ct.ThrowIfCancellationRequested(); if (version != Volatile.Read(ref readVersion)) return;
            ClearResults(); foreach (var c in columns) Columns.Add(c); foreach (var r in rows) Rows.Add(r); foreach (var m in metrics) Metrics.Add(m);
            foreach (var r in revenue) Revenue.Add(r); foreach (var c in clients) Clients.Add(c);
            Total = total; PageInfo = AnalyticsVisible ? $"Zaległe: {Rows.Count} z {total}" : $"Strona {page} · {total} wpisów";
            Status = warning ?? (Rows.Count == 0 && !AnalyticsVisible ? "Brak wyników dla podanych kryteriów." : "Dane aktualne.");
        }
        finally { if (version == Volatile.Read(ref readVersion)) Busy = false; }
    }
    public static string Number(double? n) => n?.ToString("N2", Polish) ?? "—";
    public static string Money(double? n) => n is null ? "—" : Number(n) + " zł";
    public static string Percent(double? n) => n is null ? "—" : Number(n) + "%";
    private static string Date(DateOnly? d) => d?.ToString("dd.MM.yyyy", Polish) ?? "—";
    private static string State(string s) => s switch { "draft" => "Robocze", "quoted" => "Wycenione", "accepted" => "Przyjęte", "in_production" => "W produkcji", "gotowe" => "Gotowe", "wydane" => "Wydane", "rejected" => "Odrzucone", _ => s };
}
