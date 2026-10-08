using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrdersViewModel(CrmClient api) : ObservableObject
{
    private int requestVersion;
    [ObservableProperty] private bool supportsResources;
    [ObservableProperty] private bool supportsAttachments;
    [ObservableProperty] private bool supportsDocuments;
    [ObservableProperty] private bool supportsTemplates;
    [ObservableProperty] private bool supportsIntake;
    public bool CanDocuments => SupportsDocuments && Detail is not null && !Busy;
    public bool CanSaveTemplate => SupportsTemplates && api.Session?.Role == "technolog" && CanEdit;
    partial void OnSupportsDocumentsChanged(bool value) => NotifyActions();
    partial void OnSupportsTemplatesChanged(bool value) => NotifyActions();
    public bool CanAttachments => SupportsAttachments && Detail is not null && !Busy;
    partial void OnSupportsAttachmentsChanged(bool value) => NotifyActions();
    public bool CanResources => SupportsResources && Detail is not null && !Busy;
    partial void OnSupportsResourcesChanged(bool value) => NotifyActions();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private bool archived;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private OrderDto? detail;
    [ObservableProperty] private bool detailVisible;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private bool needsRefresh;
    public bool ListVisible => !DetailVisible;
    public bool CanCreate => api.Session?.Role is "biuro" or "technolog";
    public bool CanEdit => CanCreate && Detail is { ArchivedAt: null } && !Busy && !NeedsRefresh;
    public bool CanTriage => CanEdit && Detail?.Status is "draft" or "rejected" or "standard" or "niestandard";
    public bool CanQuote => api.Session?.Role == "technolog" && CanEdit &&
        (Detail?.Status is "standard" or "niestandard" or "quoted" || Detail is { Status: "in_production", IsInternal: true });
    public bool CanConfirm => CanEdit && Detail?.Status == "quoted";
    public bool CanDeliver => CanEdit && (Detail?.Status == "gotowe" || api.Session?.Role == "technolog" && Detail?.Status == "in_production");
    public string DeliveryAction => Detail?.Status == "in_production" ? "Zakończ zlecenie" : "Wydaj klientowi";
    public bool CanArchive => api.Session?.Role == "technolog" && CanEdit && Detail?.Status is "wydane" or "rejected";
    public bool CanRestore => api.Session?.Role == "technolog" && Detail?.ArchivedAt is not null && !Busy && !NeedsRefresh;
    public bool CanTransition(string action) => action switch
    { "confirm" => CanConfirm, "deliver" => CanDeliver, "archive" => CanArchive, "restore" => CanRestore, _ => false };
    public string Heading => Detail is null ? "Zlecenia" : $"Zlecenie {Detail.OrderNumber ?? $"#{Detail.Id}"}";
    public string DetailStatus => Detail is null ? "" : OrderLabels.Status(Detail.Status);
    public string Description => Detail?.Description ?? Detail?.Notes ?? "Brak opisu.";
    public string Details => Detail is null ? "" : string.Join("\n", new[]
    {
        $"Klient: {Detail.Client}", $"Termin: {Detail.Deadline:dd.MM.yyyy}",
        $"Rodzaj: {OrderLabels.Type(Detail.OrderType)} · {Detail.Quantity ?? 1} szt.",
        $"Materiał: {Detail.Material ?? "—"}", $"Kontakt: {Detail.Contact ?? "—"}",
        $"Dostawa: {Detail.DeliveryAddress ?? "—"}", $"Rysunek: {Detail.DrawingNumber ?? "—"}",
        $"Wymiary: {Detail.Dimensions ?? "—"}", $"Masa: {Detail.WeightKg?.ToString("N3") ?? "—"} kg",
        Detail.IsInternal ? "Zlecenie wewnętrzne" : "Zlecenie zewnętrzne",
        Detail.IsDefence ? "Projekt zbrojeniowy / MON" : "",
        Detail.ArchivedAt is null ? "" : "Archiwum — tylko do odczytu"
    }.Where(s => s.Length != 0));
    public ObservableCollection<OrderListRow> Rows { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    partial void OnDetailVisibleChanged(bool value) => OnPropertyChanged(nameof(ListVisible));
    partial void OnBusyChanged(bool value) => NotifyActions();
    partial void OnNeedsRefreshChanged(bool value) => NotifyActions();
    partial void OnDetailChanged(OrderDto? value)
    {
        foreach (var name in new[] { nameof(Heading), nameof(Description), nameof(Details), nameof(DetailStatus) }) OnPropertyChanged(name);
        NotifyActions();
    }
    public void NotifyActions()
    {
        foreach (var name in new[] { nameof(CanDocuments), nameof(CanSaveTemplate), nameof(CanAttachments), nameof(CanResources), nameof(CanCreate), nameof(CanEdit), nameof(CanTriage), nameof(CanQuote), nameof(CanConfirm), nameof(CanDeliver), nameof(DeliveryAction), nameof(CanArchive), nameof(CanRestore) }) OnPropertyChanged(name);
    }
    public async Task Load(CancellationToken ct)
    {
        var version = Interlocked.Increment(ref requestVersion);
        Busy = true; Status = "Wczytywanie zleceń…";
        try
        {
            var result = await api.ReadOrders<Page<OrderDto>>($"?q={Uri.EscapeDataString(Search)}&status={Uri.EscapeDataString(Filter)}&archived={Archived.ToString().ToLowerInvariant()}&page={Page}&pageSize=50", ct);
            ct.ThrowIfCancellationRequested();
            if (version != requestVersion) return;
            Rows.Clear(); foreach (var order in result.Items) Rows.Add(new(order));
            Total = result.Total; PageInfo = $"Strona {result.PageNumber} · {Total} zleceń";
            Status = Rows.Count == 0 ? "Brak zleceń spełniających kryteria." : "Dane aktualne.";
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public async Task Open(long id, CancellationToken ct)
    {
        var version = Interlocked.Increment(ref requestVersion);
        Busy = true; Status = "Wczytywanie zlecenia…";
        try
        {
            var order = await api.ReadOrders<OrderDto>($"/{id}", ct);
            var history = await api.ReadOrders<OrderEventDto[]>($"/{id}/events", ct);
            ct.ThrowIfCancellationRequested();
            if (version != requestVersion) return;
            Detail = order; NeedsRefresh = false; History.Clear();
            foreach (var item in history) History.Add($"{item.CreatedAt:dd.MM.yyyy HH:mm} · {item.UserName ?? "—"}\n{item.EventType} · {item.Note}");
            DetailVisible = true; Status = "Dane aktualne.";
        }
        finally { if (version == requestVersion) Busy = false; }
    }
    public void Clear()
    {
        Interlocked.Increment(ref requestVersion); Rows.Clear(); History.Clear(); Detail = null; DetailVisible = false;
        Search = Filter = Status = PageInfo = ""; Page = 1; Total = 0; Busy = Archived = NeedsRefresh = SupportsResources = SupportsAttachments = SupportsDocuments = SupportsTemplates = SupportsIntake = false;
        NotifyActions();
    }
    public void AcceptSaved(OrderDto order) { Interlocked.Increment(ref requestVersion); Detail = order; History.Clear(); DetailVisible = true; NeedsRefresh = false; }
}

public sealed record OrderListRow(OrderDto Order)
{
    public string Number => Order.OrderNumber ?? $"#{Order.Id}";
    public string PendingQuestions => Order.PendingQuestions > 0 ? $"! {Order.PendingQuestions}" : "";
    public string Client => Order.Client;
    public string Description => Order.Description ?? Order.Notes ?? "—";
    public string Status => OrderLabels.Status(Order.Status);
    public string Deadline => Order.Deadline?.ToString("dd.MM.yyyy") ?? "—";
    public string Kind => Order.IsInternal ? "Wewnętrzne" : OrderLabels.Type(Order.OrderType);
}

public static class OrderLabels
{
    public static Dictionary<string, string> Statuses { get; } = new()
    {
        [""] = "Wszystkie stany", ["draft"] = "Nowe", ["standard"] = "Standardowe", ["niestandard"] = "Do wyceny",
        ["quoted"] = "Wycena do zatwierdzenia", ["in_production"] = "W realizacji", ["gotowe"] = "Gotowe do wydania",
        ["wydane"] = "Zakończone", ["rejected"] = "Odrzucone"
    };
    public static Dictionary<string, string> Types { get; } = new()
    { ["remont"] = "Remont", ["nowa_czesc"] = "Nowa część", ["catalog"] = "Katalog", ["zbrojenie"] = "Zbrojenie / MON" };
    public static string Status(string value) => Statuses.GetValueOrDefault(value, value);
    public static string Type(string? value) => value is null ? "—" : Types.GetValueOrDefault(value, value);
}
