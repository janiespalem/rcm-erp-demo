using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class TetrapodViewModel : ObservableObject
{
    private readonly ITetrapodDeliveryStore store;
    public long UserId { get; }
    public Func<bool>? AdditionalDirty { get; set; }
    public TetrapodViewModel(long userId = 0, ITetrapodDeliveryStore? deliveryStore = null) { UserId = userId; store = deliveryStore ?? new TetrapodDeliveryStore(); }
    public ObservableCollection<TetrapodDeliveryRow> Deliveries { get; } = [];
    [ObservableProperty] private string newDelivery6 = "";
    [ObservableProperty] private string newDelivery12 = "";
    [ObservableProperty] private string newDelivery16 = "";
    [ObservableProperty] private string deliveryStatus = "Historia dostaw jest zapisywana lokalnie, osobno dla Twojego konta.";
    [ObservableProperty] private bool historyLoaded;
    [ObservableProperty] private bool historyDirty;
    [ObservableProperty] private bool deliveryAccess;
    [ObservableProperty] private bool useHistory;
    public bool CanManageDeliveries => CanEdit && HistoryLoaded && DeliveryAccess && UserId > 0;
    public bool CanSaveDeliveries => CanManageDeliveries && HistoryDirty;
    public bool CanReadDeliveries => CanEdit && !HistoryLoaded && DeliveryAccess && UserId > 0;
    public bool HasDeliveryDraft => NewDelivery6 != "" || NewDelivery12 != "" || NewDelivery16 != "";
    public bool LocalDirty => Planned != "" || Completed != "0" || Delivery6 != "" || Delivery12 != "" || Delivery16 != "" || HistoryDirty || HasDeliveryDraft;
    public string DeliveryTotals => string.Format(CultureInfo.GetCultureInfo("pl-PL"), "Suma {0} dostaw: Ø6 {1:N3} kg · Ø12 {2:N3} kg · Ø16 {3:N3} kg", Deliveries.Count, Total(6), Total(12), Total(16));
    private double Total(int diameter) => Math.Round(Deliveries.Sum(row => diameter switch { 6 => row.Entry.Kg6, 12 => row.Entry.Kg12, _ => row.Entry.Kg16 }), 3, MidpointRounding.AwayFromZero);
    private void ChangedHistory()
    {
        HistoryDirty = true; Result = null; OnPropertyChanged(nameof(DeliveryTotals)); OnPropertyChanged(nameof(Dirty));
        DeliveryStatus = "Historia zmieniona. Wybierz Zapisz historię, aby zachować ją na tym komputerze.";
    }
    public bool AddDelivery(DateTimeOffset? receivedAt = null)
    {
        if (!CanManageDeliveries) return false;
        try
        {
            double Kg(string text) => string.IsNullOrWhiteSpace(text) ? 0 : Number(text, "Masa dostawy");
            var kg6 = Kg(NewDelivery6); var kg12 = Kg(NewDelivery12); var kg16 = Kg(NewDelivery16);
            if (kg6 + kg12 + kg16 <= 0 || Math.Max(kg6, Math.Max(kg12, kg16)) > 1_000_000_000) throw new FormatException("Wpisz dodatnią masę dla co najmniej jednej średnicy, maksymalnie 1 000 000 000 kg.");
            if (Deliveries.Count >= 5000) throw new FormatException("Historia zawiera już 5000 dostaw.");
            Deliveries.Insert(0, new(new(Guid.NewGuid(), receivedAt ?? DateTimeOffset.UtcNow, kg6, kg12, kg16)));
            NewDelivery6 = NewDelivery12 = NewDelivery16 = ""; UseHistory = true; ChangedHistory(); return true;
        }
        catch (FormatException ex) { DeliveryStatus = ex.Message; return false; }
    }
    public bool RemoveDelivery(Guid id)
    {
        if (!CanManageDeliveries || Deliveries.FirstOrDefault(row => row.Entry.Id == id) is not { } row) return false;
        Deliveries.Remove(row); ChangedHistory(); return true;
    }
    public async Task<bool> LoadDeliveries(CancellationToken ct)
    {
        if (!CanReadDeliveries) return HistoryLoaded;
        IsCalculating = true;
        try
        {
            var rows = await store.Read(UserId, ct); ct.ThrowIfCancellationRequested();
            Deliveries.Clear(); foreach (var row in rows) Deliveries.Add(new(row));
            HistoryLoaded = true; HistoryDirty = false; if (rows.Length > 0) UseHistory = true; Result = null; OnPropertyChanged(nameof(DeliveryTotals));
            DeliveryStatus = "Historia dostaw wczytana z tego komputera."; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        { DeliveryStatus = "Nie udało się odczytać historii dostaw. Wpisane wartości zachowano. Ponów wczytanie; istniejący plik pozostaje bez zmian."; return false; }
        finally { IsCalculating = false; }
    }
    public async Task<bool> SaveDeliveries(CancellationToken ct)
    {
        if (!CanSaveDeliveries) return false;
        var snapshot = Deliveries.Select(row => row.Entry).ToArray(); IsCalculating = true;
        try
        {
            await store.Write(UserId, snapshot, ct); HistoryDirty = false;
            DeliveryStatus = "Zapisano historię dostaw na tym komputerze dla Twojego konta."; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        { DeliveryStatus = "Nie udało się zapisać historii dostaw. Pozycje zachowano; wybierz Zapisz historię ponownie."; return false; }
        finally { IsCalculating = false; }
    }
    [ObservableProperty] private string planned = "";
    [ObservableProperty] private string completed = "0";
    [ObservableProperty] private string delivery6 = "";
    [ObservableProperty] private string delivery12 = "";
    [ObservableProperty] private string delivery16 = "";
    [ObservableProperty] private string status = "Wpisz plan i wykonanie, a następnie wybierz Oblicz.";
    [ObservableProperty] private bool isCalculating;
    [ObservableProperty] private TetrapodPlan? result;
    public bool CanEdit => !IsCalculating;
    public bool HasResult => Result is not null;
    public bool Dirty => LocalDirty || AdditionalDirty?.Invoke() == true;
    public string Summary => Result is { } r
        ? string.Format(CultureInfo.GetCultureInfo("pl-PL"), "Do wykonania: {0:N0} tetrapodów · {1:N0} koszy · {2:N3} kg zbrojenia",
            r.Tetrapods.Remaining, r.Baskets.Remaining, r.WeightsKg.Remaining) : "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Planned) or nameof(Completed) or nameof(Delivery6) or nameof(Delivery12) or nameof(Delivery16))
        {
            Result = null;
            OnPropertyChanged(nameof(Dirty));
            Status = "Wartości zmienione. Wybierz Oblicz, aby odświeżyć wynik.";
        }
        if (e.PropertyName is nameof(NewDelivery6) or nameof(NewDelivery12) or nameof(NewDelivery16) or nameof(HistoryDirty) or nameof(UseHistory)) OnPropertyChanged(nameof(Dirty));
        if (e.PropertyName == nameof(UseHistory)) Result = null;
        if (e.PropertyName is nameof(IsCalculating) or nameof(HistoryLoaded) or nameof(HistoryDirty) or nameof(DeliveryAccess))
        { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanManageDeliveries)); OnPropertyChanged(nameof(CanSaveDeliveries)); OnPropertyChanged(nameof(CanReadDeliveries)); }
        if (e.PropertyName == nameof(Result)) { OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(Summary)); }
    }

    private static double Number(string value, string label, bool whole = false)
    {
        if (!double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || (whole && Math.Truncate(number) != number))
            throw new FormatException(label + (whole ? ": wpisz pełną liczbę nieujemną." : ": wpisz nieujemną liczbę, np. 125,5."));
        return number;
    }
    public async Task<bool> Calculate(CrmClient api, CancellationToken ct)
    {
        if (IsCalculating) return false;
        Result = null;
        try
        {
            if (UserId > 0 && DeliveryAccess && UseHistory && !HistoryLoaded) throw new FormatException("Nie odczytano historii dostaw. Ponów wczytanie lub wyłącz sumę historii i podaj ręczny bilans.");
            var plan = Number(Planned, "Plan", true);
            var done = Number(Completed, "Wykonano", true);
            if (done > plan) throw new FormatException("Wykonana liczba nie może przekraczać planu.");
            double? Delivery(string value, int diameter) => string.IsNullOrWhiteSpace(value) ? null : Number(value, $"Dostawa Ø{diameter}");
            var totals = UseHistory && HistoryLoaded
                ? new Dictionary<int, double?> { [6] = Total(6), [12] = Total(12), [16] = Total(16) }
                : new Dictionary<int, double?> { [6] = Delivery(Delivery6, 6), [12] = Delivery(Delivery12, 12), [16] = Delivery(Delivery16, 16) };
            var input = new TetrapodInput(plan, done, totals);
            IsCalculating = true; Status = "Obliczanie…";
            var calculated = await api.CalculateTetrapod(input, ct);
            ct.ThrowIfCancellationRequested();
            Result = calculated;
            Status = "Obliczono. Wynik dotyczy wpisanych wartości; nie zmienia stanu magazynu.";
            return true;
        }
        catch (FormatException ex) { Status = ex.Message; }
        catch (ApiFailure ex)
        {
            Status = ex.Message;
            if (ex.Errors.Count > 0) Status += " " + string.Join(" ", ex.Errors.Values.SelectMany(x => x));
            if (ex.Status == 401) throw;
        }
        catch (HttpRequestException) { Status = "Brak połączenia. Sprawdź internet i spróbuj ponownie. Wpisane wartości zachowano."; }
        catch (System.Text.Json.JsonException) { Status = "Nie można odczytać wyniku z serwera. Wpisane wartości zachowano; spróbuj ponownie."; }
        catch (OperationCanceledException) { Status = "Przerwano oczekiwanie. Wpisane wartości zachowano; możesz obliczyć ponownie."; }
        finally { IsCalculating = false; }
        return false;
    }
}

public sealed record TetrapodDeliveryRow(TetrapodDelivery Entry)
{
    private DateTimeOffset LocalTime => TimeZoneInfo.ConvertTime(Entry.ReceivedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
    public string Date => LocalTime.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("pl-PL"));
    public string Weekday => LocalTime.ToString("dddd", CultureInfo.GetCultureInfo("pl-PL"));
    public string Time => LocalTime.ToString("HH:mm", CultureInfo.GetCultureInfo("pl-PL"));
}
