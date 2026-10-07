using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class ShiftReportsViewModel(CrmClient api) : ObservableObject
{
    private int epoch;
    private long? entryUser;
    public ShiftReportFeatures Features { get; private set; } = new(false, false, false, new Dictionary<int, IReadOnlyList<ShiftReportQuestion>>());
    [ObservableProperty] private bool busy;
    [ObservableProperty] private DateTime? dateFrom;
    [ObservableProperty] private DateTime? dateTo;
    [ObservableProperty] private DateTime? entryDate;
    [ObservableProperty] private string entryShift = "I";
    [ObservableProperty] private string shiftFilter = "";
    [ObservableProperty] private bool deleted;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private ShiftReportsListRow? selected;
    public ObservableCollection<ShiftReportsListRow> Rows { get; } = [];
    public ObservableCollection<ShiftReportsListRow> Today { get; } = [];
    public bool CanCreate => Features.Write && api.Session?.Role is "produkcja" or "technolog" && !Busy;
    public bool CanSeeDeleted => api.Session?.Role is "technolog" or "ceo";
    public bool CanOpen => Features.Read && Selected is not null && !Busy;
    public bool CanOpenDate => Features.Read && !Busy;
    public bool CanPrevious => !Busy && Page > 1;
    public bool CanNext => !Busy && Page * 50 < Total;
    public string AccessHint => !Features.Read ? "Ten serwer nie udostępnia raportów zmianowych." : !Features.Write ? "Raporty tylko do odczytu." : "Wybierz raport lub otwórz datę i zmianę. Szkic zapisujesz jawnie w formularzu.";
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnPageChanged(int value) => Notify();
    partial void OnTotalChanged(int value) => Notify();
    partial void OnSelectedChanged(ShiftReportsListRow? value) => Notify();
    public void SetFeatures(ShiftReportFeatures value)
    {
        if (entryUser != api.Session?.UserId)
        {
            EntryShift = api.Session?.DefaultShift is "II" ? "II" : "I";
            entryUser = api.Session?.UserId;
        }
        Features = value; Notify();
    }
    public void Notify() { foreach (var key in new[] { nameof(CanCreate), nameof(CanSeeDeleted), nameof(CanOpen), nameof(CanOpenDate), nameof(CanPrevious), nameof(CanNext), nameof(AccessHint) }) OnPropertyChanged(key); }
    private static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public async Task Load(CancellationToken ct)
    {
        if (!Features.Read) return;
        var version = Interlocked.Increment(ref epoch); Busy = true; Status = "Wczytywanie raportów…";
        try
        {
            var query = $"?deleted={(CanSeeDeleted && Deleted).ToString().ToLowerInvariant()}&page={Page}&pageSize=50";
            if (DateFrom is not null) query += "&dateFrom=" + Date(DateFrom);
            if (DateTo is not null) query += "&dateTo=" + Date(DateTo);
            if (!string.IsNullOrEmpty(ShiftFilter)) query += "&shift=" + Uri.EscapeDataString(ShiftFilter);
            var listTask = api.ReadShiftReports<Page<ShiftReportDto>>(query, ct);
            var todayTask = api.ReadShiftReports<ShiftReportToday>("/today", ct);
            await Task.WhenAll(listTask, todayTask); ct.ThrowIfCancellationRequested(); if (version != epoch) return;
            var list = await listTask; var today = await todayTask;
            if (list.Items.Any(row => row.Id <= 0 || row.Version <= 0)) throw new ApiFailure(502, "Nieprawidłowa lista raportów.");
            var selected = Selected?.Report.Id; Rows.Clear(); foreach (var row in list.Items) Rows.Add(new(row)); Selected = Rows.FirstOrDefault(row => row.Report.Id == selected);
            Today.Clear(); foreach (var row in today.Reports) Today.Add(new(row));
            EntryDate ??= today.Date.ToDateTime(TimeOnly.MinValue);
            var totals = today.Totals; Summary = $"Dzisiaj {today.Date:dd.MM.yyyy} · Zakończone raporty: {totals.Reports}\nZłożone: {totals.Assembled} · Przygotowane: {totals.Prepared} · Zalane: {totals.Poured} · Sprawdzone: {totals.Checked} · Rozebrane: {totals.Demoulded} · Uszkodzone: {totals.Damaged}";
            Page = list.PageNumber; Total = list.Total; PageInfo = $"Strona {Page} · {Total} raportów"; Status = Rows.Count == 0 ? "Brak raportów dla wybranych filtrów." : "Dane aktualne.";
        }
        finally { if (version == epoch) Busy = false; }
    }
    public async Task<ShiftReportDto?> Open(long id, CancellationToken ct)
    {
        if (!Features.Read || Busy) return null; var version = Interlocked.Increment(ref epoch); Busy = true;
        try { var result = await api.ReadShiftReports<ShiftReportDto>($"/{id}", ct); ct.ThrowIfCancellationRequested(); if (version != epoch) return null; if (result.Id != id || result.Version <= 0) throw new ApiFailure(502, "Nieprawidłowy raport."); return result; }
        finally { if (version == epoch) Busy = false; }
    }
    public void CancelRead() { Interlocked.Increment(ref epoch); Busy = false; }
    public void Clear() { CancelRead(); Rows.Clear(); Today.Clear(); Selected = null; DateFrom = DateTo = EntryDate = null; ShiftFilter = Status = Summary = PageInfo = ""; EntryShift = "I"; entryUser = null; Deleted = false; Page = 1; Total = 0; Features = new(false, false, false, new Dictionary<int, IReadOnlyList<ShiftReportQuestion>>()); Notify(); }
}
