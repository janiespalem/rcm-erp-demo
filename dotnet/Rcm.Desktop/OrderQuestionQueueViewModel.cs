using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderQuestionQueueViewModel(CrmClient api) : ObservableObject
{
    private int readVersion;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool available;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int total;
    [ObservableProperty] private QuestionQueueItem? selected;
    public ObservableCollection<QuestionQueueItem> Rows { get; } = [];
    public bool CanOpen => Available && !Busy && Selected is not null && api.Session?.Role is "biuro" or "technolog";
    public bool CanPrevious => Available && !Busy && Page > 1;
    public bool CanNext => Available && !Busy && Page * 50 < Total;
    partial void OnBusyChanged(bool value) => NotifyActions();
    partial void OnAvailableChanged(bool value) => NotifyActions();
    partial void OnSelectedChanged(QuestionQueueItem? value) => NotifyActions();
    partial void OnPageChanged(int value) => NotifyActions();
    partial void OnTotalChanged(int value) => NotifyActions();
    public void NotifyActions()
    {
        OnPropertyChanged(nameof(CanOpen)); OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(CanNext));
    }
    public void CancelRead() { Interlocked.Increment(ref readVersion); Busy = false; }
    public void Clear()
    {
        CancelRead(); Rows.Clear(); Selected = null; Search = Filter = Status = PageInfo = "";
        Page = 1; Total = 0; Available = false;
    }
    public async Task Load(CancellationToken ct)
    {
        if (!Available) return;
        var version = Interlocked.Increment(ref readVersion);
        var page = Page;
        Busy = true; Status = "Wczytywanie pytań…";
        try
        {
            var result = await api.ReadOrderQuestionQueue(Search, Filter, page, ct);
            ct.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref readVersion)) return;
            var selectedId = Selected?.Question.Id;
            Rows.Clear(); foreach (var question in result.Items) Rows.Add(new(question));
            Selected = Rows.SingleOrDefault(r => r.Question.Id == selectedId);
            Total = result.Total;
            PageInfo = $"Strona {result.PageNumber} · {Total} pytań";
            Status = Rows.Count == 0 ? "Brak pytań spełniających kryteria." : "Dane aktualne. Otwórz pytanie, aby przeczytać lub udzielić odpowiedzi.";
        }
        finally { if (version == Volatile.Read(ref readVersion)) Busy = false; }
    }
    public async Task<OrderDto?> OpenSelected(CancellationToken ct)
    {
        if (!CanOpen || Selected is not { } selected) return null;
        var version = Interlocked.Increment(ref readVersion);
        Busy = true;
        try
        {
            var order = await api.ReadOrders<OrderDto>($"/{selected.Question.OrderId}", ct);
            ct.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref readVersion)) return null;
            if (order.Id != selected.Question.OrderId) throw new ApiFailure(502, "Serwer zwrócił inne zlecenie.");
            return order;
        }
        finally { if (version == Volatile.Read(ref readVersion)) Busy = false; }
    }
}

public sealed record QuestionQueueItem(OrderQuestionQueueRow Question)
{
    public string Number => Question.OrderNumber ?? $"#{Question.OrderId}";
    public string Client => Question.Client;
    public string Deadline => Question.Deadline?.ToString("dd.MM.yyyy") ?? "—";
    public string QuestionText => Question.QuestionText;
    public string AnswerText => Question.AnswerText ?? "—";
    public string AskedAt => Question.AskedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string Status => Question.Status == "pending" ? "Czeka na odpowiedź" : "Odpowiedziano";
    public bool Urgent => Question.Status == "pending" && Question.Deadline is { } date
        && date <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Europe/Warsaw").DateTime).AddDays(3);
    public string DeadlineHint => Urgent ? "PILNE · " + Deadline : Deadline;
    public string Archive => Question.ArchivedAt is null ? "" : "Archiwum — podgląd";
}
