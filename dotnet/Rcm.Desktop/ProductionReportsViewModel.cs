using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed record ProductionReportFilter(string Key, string Label);
public sealed partial class ProductionReportsViewModel(CrmClient api) : ObservableObject
{
    private int epoch;
    public ProductionReviewFeatures Features { get; private set; } = new(false, false, false);
    public Guid? ContractId { get; private set; }
    public ObservableCollection<ProductionReportRow> Reports { get; } = [];
    public ObservableCollection<ProductionReportRow> Queue { get; } = [];
    public static IReadOnlyList<ProductionReportFilter> Filters { get; } = [new("all", "Wszystkie"), new("draft", "Szkice"), new("pending", "Do sprawdzenia"), new("accepted", "Przyjęte"), new("returned", "Zwrócone")];
    [ObservableProperty] private ProductionReportFilter filter = Filters[0];
    [ObservableProperty] private ProductionReportRow? selectedReport;
    [ObservableProperty] private ProductionReportRow? selectedQueue;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private int queuePage = 1;
    [ObservableProperty] private int queueTotal;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private ProductionAcceptedOperations? operations;
    public bool Available => Features.Read;
    public bool CanReview => Features.Read && Features.Review;
    public bool CanLoad => Features.Read && ContractId is not null && !Busy;
    public bool CanOpenReport => CanLoad && SelectedReport is not null;
    public bool CanOpenQueue => Features.Review && !Busy && SelectedQueue is not null;
    public bool CanPrevious => CanLoad && Page > 1;
    public bool CanNext => CanLoad && Page * 50 < Total;
    public bool CanPreviousQueue => Features.Review && !Busy && QueuePage > 1;
    public bool CanNextQueue => Features.Review && !Busy && QueuePage * 50 < QueueTotal;
    public string PageInfo => $"Strona {Page} · {Total} raportów";
    public string QueuePageInfo => $"Strona {QueuePage} · {QueueTotal} raportów";
    public string Forms => Operations is { } value ? $"Formy · złożone: {value.Assembled} · przygotowane: {value.Prepared} · sprawdzone: {value.Checked}" : "Formy · brak wczytanego podsumowania";
    public string Products => Operations is { } value ? $"Zalane: {value.Poured} · Rozformowane: {value.Demoulded} · Uszkodzone: {value.Damaged}" : "Zalane / rozformowane · brak wczytanego podsumowania";
    public string Summary => Operations is { } value ? $"Operacje z {value.Reports} przyjętych raportów w aktualnej wersji. Korekta lub zmiana kontraktu wymaga ponownego sprawdzenia." : "Wczytaj raporty przypisane do kontraktu.";
    partial void OnFilterChanged(ProductionReportFilter value) => Page = 1;
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnOperationsChanged(ProductionAcceptedOperations? value) => Notify();
    partial void OnSelectedReportChanged(ProductionReportRow? value) => Notify();
    partial void OnSelectedQueueChanged(ProductionReportRow? value) => Notify();
    partial void OnPageChanged(int value) => Notify();
    partial void OnTotalChanged(int value) => Notify();
    partial void OnQueuePageChanged(int value) => Notify();
    partial void OnQueueTotalChanged(int value) => Notify();
    public void Notify() { foreach (var name in new[] { nameof(Available), nameof(CanReview), nameof(CanLoad), nameof(CanOpenReport), nameof(CanOpenQueue), nameof(CanPrevious), nameof(CanNext), nameof(CanPreviousQueue), nameof(CanNextQueue), nameof(PageInfo), nameof(QueuePageInfo), nameof(Forms), nameof(Products), nameof(Summary) }) OnPropertyChanged(name); }
    public async Task Initialize(CancellationToken ct)
    {
        if (api.Session?.ProductionEnabled != true) { Clear(); return; }
        try { Features = await api.ReadProduction<ProductionReviewFeatures>("/review/features", ct); }
        catch (ApiFailure ex) when (ex.Status == 404) { Features = new(false, false, false); }
        ct.ThrowIfCancellationRequested();
        if (!Features.Review) { Queue.Clear(); SelectedQueue = null; QueueTotal = 0; }
        if (!Features.Read) { Reports.Clear(); SelectedReport = null; Total = 0; Operations = null; }
        Notify();
    }
    public void SelectContract(Guid? id)
    {
        if (ContractId == id) return; Cancel(); ContractId = id; Reports.Clear(); SelectedReport = null; Operations = null; Page = 1; Total = 0; Notify();
    }
    public async Task Load(CancellationToken ct)
    {
        if (!Features.Read || ContractId is null) return;
        var id = ContractId.Value; var version = Interlocked.Increment(ref epoch); Busy = true;
        try
        {
            var rows = await api.ReadProduction<Page<ProductionReportReviewState>>($"/contracts/{id}/reports?state={Filter.Key}&page={Page}&pageSize=50", ct);
            var operations = await api.ReadProduction<ProductionAcceptedOperations>($"/contracts/{id}/accepted-operations", ct); ct.ThrowIfCancellationRequested(); if (version != epoch) return;
            if (operations.ContractId != id || rows.Items.Any(row => row.Link?.ContractId != id)) throw new ApiFailure(502, "Nieprawidłowe dane raportów kontraktu.");
            Reports.Clear(); foreach (var item in rows.Items) Reports.Add(new(item)); SelectedReport = null; Page = rows.PageNumber; Total = rows.Total; Operations = operations;
        }
        finally { if (version == epoch) Busy = false; }
    }
    public async Task LoadQueue(CancellationToken ct)
    {
        if (!Features.Review) return; var version = Interlocked.Increment(ref epoch); Busy = true;
        try
        {
            var rows = await api.ReadProduction<Page<ProductionReportReviewState>>($"/review/queue?state=pending&page={QueuePage}&pageSize=50", ct); ct.ThrowIfCancellationRequested(); if (version != epoch) return;
            Queue.Clear(); foreach (var item in rows.Items) Queue.Add(new(item)); SelectedQueue = null; QueuePage = rows.PageNumber; QueueTotal = rows.Total;
        }
        finally { if (version == epoch) Busy = false; }
    }
    public void Cancel() { Interlocked.Increment(ref epoch); Busy = false; }
    public void Clear() { Cancel(); Features = new(false, false, false); ContractId = null; Reports.Clear(); Queue.Clear(); SelectedReport = SelectedQueue = null; Operations = null; Page = QueuePage = 1; Total = QueueTotal = 0; Notify(); }
}
