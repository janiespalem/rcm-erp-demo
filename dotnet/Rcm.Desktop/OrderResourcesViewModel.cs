using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderResourcesViewModel(OrderDto order) : ObservableObject
{
    private enum Editor { None, Hours, Question, Answer }
    private Editor editor;
    private string baseline = "";
    private string? role;
    private object? pending;
    private string pendingPath = "";
    private bool blocked;
    public OrderDto Order { get; } = order;
    public ObservableCollection<OrderOperationRow> Operations { get; } = [];
    public ObservableCollection<OrderQuestionDto> Questions { get; } = [];
    public OrderOperationRow? SelectedOperation { get; private set; }
    public OrderQuestionDto? SelectedQuestion { get; private set; }
    [ObservableProperty] private string draft = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private bool loaded;
    public bool Dirty => Draft != baseline || Uncertain;
    public bool CanNavigate => Loaded && !Busy && !Uncertain;
    public bool CanReload => !Busy && !Uncertain;
    public bool CanAsk => CanNavigate && Order.ArchivedAt is null && role == "technolog";
    private bool Allowed => Order.ArchivedAt is null && !blocked && (editor switch
    {
        Editor.Hours or Editor.Question => role == "technolog",
        Editor.Answer => role is "biuro" or "technolog" && SelectedQuestion?.Status == "pending",
        _ => false
    });
    public bool CanEdit => Loaded && !Busy && !Uncertain && Allowed;
    public bool ReadOnly => !CanEdit;
    public bool CanSave => Loaded && !Busy && (Uncertain ? pending is not null : Allowed && Dirty);
    public string SaveLabel => Uncertain ? "Ponów ten sam zapis" : editor switch
    { Editor.Hours => "Zapisz godziny", Editor.Question => "Wyślij pytanie", _ => "Zapisz odpowiedź" };
    public string EditorTitle => editor switch
    { Editor.Hours => SelectedOperation?.Name ?? "Operacja", Editor.Question => "Nowe pytanie", Editor.Answer => "Pytanie o parametry", _ => "Wybierz operację lub pytanie" };
    public string DraftLabel => editor == Editor.Hours ? "Rzeczywisty czas (godziny)" : editor == Editor.Question ? "Treść pytania" : "Odpowiedź";
    public bool HasEditor => editor != Editor.None;
    public string Context => SelectedOperation is { } op
        ? $"Dział: {op.Department ?? "—"}\nOdpowiedzialny: {op.Responsible ?? "—"}\nKolejność: {op.Sequence?.ToString() ?? "—"}\nRzeczywisty czas: {op.ActualHours?.ToString("N2") ?? "—"} h"
        : SelectedQuestion is { } q
            ? $"{q.QuestionText}\n\nZadano: {q.AskedAt.ToLocalTime():dd.MM.yyyy HH:mm}\n" +
              (q.Status == "pending" ? "Oczekuje na odpowiedź" : $"Odpowiedź: {q.AnswerText}\n{q.AnsweredAt?.ToLocalTime():dd.MM.yyyy HH:mm}")
            : "";
    public string AccessHint => Order.ArchivedAt is not null ? "Zlecenie w archiwum — tylko do odczytu." :
        role == "ceo" ? "Podgląd operacji i pytań — tylko do odczytu." : "Godziny i pytania zapisuje technolog. Biuro i technolog udzielają odpowiedzi.";

    partial void OnDraftChanged(string value)
    {
        if (Dirty && !Busy && !Uncertain) Status = "Niezapisane zmiany.";
        Notify();
    }
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    partial void OnLoadedChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(Dirty), nameof(CanNavigate), nameof(CanReload), nameof(CanAsk), nameof(CanEdit), nameof(ReadOnly), nameof(CanSave), nameof(SaveLabel),
            nameof(EditorTitle), nameof(DraftLabel), nameof(HasEditor), nameof(Context), nameof(AccessHint), nameof(SelectedOperation), nameof(SelectedQuestion) })
            OnPropertyChanged(name);
    }
    public void RefreshAccess(CrmClient api) { role = api.Session?.Role; Notify(); }
    public void DiscardDraft()
    {
        if (Busy || Uncertain) return;
        Draft = baseline; pending = null; blocked = false; Notify();
    }
    private bool Select(Editor next, OrderOperationRow? operation, OrderQuestionDto? question)
    {
        if (!CanNavigate || Dirty) return false;
        editor = next; SelectedOperation = operation; SelectedQuestion = question;
        pending = null; blocked = false;
        baseline = next == Editor.Hours ? operation?.ActualHours?.ToString(CultureInfo.CurrentCulture) ?? "" : question?.AnswerText ?? "";
        Draft = baseline; Notify(); return true;
    }
    public bool SelectOperation(OrderOperationRow row) => Operations.Contains(row) && Select(Editor.Hours, row, null);
    public bool SelectQuestion(OrderQuestionDto row) => Questions.Contains(row) && Select(Editor.Answer, null, row);
    public bool BeginQuestion() => CanAsk && Select(Editor.Question, null, null);

    public async Task Load(CrmClient api, CancellationToken ct)
    {
        if (!CanReload || Dirty) return;
        RefreshAccess(api); Busy = true; Status = "Wczytywanie operacji i pytań…";
        try
        {
            var operations = await api.ReadOrders<OrderOperationRow[]>($"/{Order.Id}/operations", ct);
            var questions = await api.ReadOrders<OrderQuestionDto[]>($"/{Order.Id}/questions", ct);
            ct.ThrowIfCancellationRequested();
            var opId = SelectedOperation?.Id; var questionId = SelectedQuestion?.Id;
            Operations.Clear(); foreach (var item in operations) Operations.Add(item);
            Questions.Clear(); foreach (var item in questions) Questions.Add(item);
            SelectedOperation = operations.SingleOrDefault(x => x.Id == opId);
            SelectedQuestion = questions.SingleOrDefault(x => x.Id == questionId);
            editor = SelectedOperation is not null ? Editor.Hours : SelectedQuestion is not null ? Editor.Answer : Editor.None;
            baseline = SelectedOperation?.ActualHours?.ToString(CultureInfo.CurrentCulture) ?? SelectedQuestion?.AnswerText ?? "";
            Draft = baseline; blocked = false; Loaded = true; Status = "Dane aktualne.";
        }
        finally { Busy = false; }
    }

    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        RefreshAccess(api);
        if (!CanSave) return false;
        if (pending is null)
        {
            if (editor == Editor.Hours)
            {
                if (!OrderEditorViewModel.TryNumber(Draft, out var hours) || !double.IsFinite(hours) || hours is < 0 or > 999_999.99)
                { Status = "Podaj liczbę godzin od 0 do 999 999,99."; return false; }
                pending = new SetOrderHours(Guid.NewGuid(), hours);
                pendingPath = $"/{Order.Id}/operations/{SelectedOperation!.Id}/hours";
            }
            else
            {
                var text = Draft.Trim();
                if (text.Length is < 1 or > 2000) { Status = "Wpisz treść od 1 do 2000 znaków."; return false; }
                pending = editor == Editor.Question ? new AskOrderQuestion(Guid.NewGuid(), text) : new AnswerOrderQuestion(Guid.NewGuid(), text);
                pendingPath = $"/{Order.Id}/questions" + (editor == Editor.Answer ? $"/{SelectedQuestion!.Id}/answer" : "");
            }
        }
        Busy = true; Status = "Zapisywanie…";
        try
        {
            if (pending is SetOrderHours)
            {
                var result = await api.WriteOrder<OrderOperationRow>(HttpMethod.Patch, pendingPath, pending, ct);
                if (result.Id != SelectedOperation!.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie operacji.");
                Operations[Operations.IndexOf(SelectedOperation)] = result; SelectedOperation = result;
            }
            else
            {
                var result = await api.WriteOrder<OrderQuestionDto>(HttpMethod.Post, pendingPath, pending, ct);
                if (result.OrderId != Order.Id || pending is AnswerOrderQuestion && result.Id != SelectedQuestion!.Id)
                    throw new ApiFailure(502, "Nieprawidłowe potwierdzenie pytania.");
                if (SelectedQuestion is null) Questions.Add(result);
                else Questions[Questions.IndexOf(SelectedQuestion)] = result;
                SelectedQuestion = result; SelectedOperation = null; editor = Editor.Answer;
                Draft = result.AnswerText ?? "";
            }
            pending = null; Uncertain = false; baseline = Draft; Status = "Zapis potwierdzony."; Notify(); return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401)
        { MarkUncertain("Zaloguj się ponownie, a następnie ponów ten sam zapis. Treść zachowano."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500)
        { MarkUncertain("Nie otrzymano potwierdzenia. Ponów ten sam zapis; treść i identyfikator zostały zachowane."); return false; }
        catch (ApiFailure ex)
        {
            pending = null; Uncertain = false; blocked = ex.Status is 403 or 404 or 409 or 410;
            Status = ex.Message + (blocked ? " Treść zachowano. Odśwież dane przed kolejnym zapisem." : " Treść zachowano — możesz ją poprawić.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { MarkUncertain("Wynik zapisu nieznany. Ponów ten sam zapis; treść i identyfikator zostały zachowane."); return false; }
        finally { Busy = false; }
    }
    private void MarkUncertain(string message) { Uncertain = true; Status = message; }
}
