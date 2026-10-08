using System.Collections;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public enum EditorKind { Customer, Topic, Conversation }
public sealed partial class EditorViewModel : ObservableObject, INotifyDataErrorInfo
{
    public EditorKind Kind { get; }
    public CustomerDto? Customer { get; }
    public TopicDto? Topic { get; }
    public Guid CustomerId { get; }
    public string Title => Kind switch { EditorKind.Customer => Customer is null ? "Dodaj klienta" : "Edytuj dane", EditorKind.Topic => Topic is null ? "Dodaj temat" : "Edytuj temat / zaplanuj kontakt", _ => "Zapisz rozmowę" };
    public bool IsCustomer => Kind == EditorKind.Customer;
    public bool IsTopic => Kind == EditorKind.Topic;
    public bool IsConversation => Kind == EditorKind.Conversation;
    public bool HasPlan => DateMode != "none" && State != "closed";
    public bool ExactDate => HasPlan && DateMode == "exact";
    public bool DescriptiveDate => HasPlan && DateMode == "text";
    public bool CanEdit => !IsSaving && !IsUncertain;
    public bool CanClose => !IsSaving;
    public bool Dirty => initial != Snapshot() || IsUncertain;
    public long ExpectedVersion { get; private set; }
    public object? Saved { get; private set; }
    private string initial = "";
    private object? pending;
    private Guid requestId = Guid.NewGuid();
    private CustomerDetail? latest;
    private readonly Dictionary<string, string[]> errors = [];
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public bool HasErrors => errors.Count != 0;
    public IEnumerable GetErrors(string? propertyName) => propertyName != null && errors.TryGetValue(propertyName, out var found) ? found : Array.Empty<string>();
    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string contactPerson = "";
    [ObservableProperty] private string phone = "";
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string source = "Telefon";
    [ObservableProperty] private string originalNote = "";
    [ObservableProperty] private bool isSynthetic;
    [ObservableProperty] private string need = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private string state = "new";
    [ObservableProperty] private string dateMode = "none";
    [ObservableProperty] private DateTime? nextDate;
    [ObservableProperty] private string nextDescription = "";
    [ObservableProperty] private string nextAction = "";
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private bool isUncertain;
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool isLifecycleRejected;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comparison = "";
    public ProductChoice[] Products { get; } = CrmVocabulary.Products.Select(p => new ProductChoice(p)).ToArray();
    public EditorViewModel(EditorKind kind, Guid customerId = default, CustomerDto? customer = null, TopicDto? topic = null)
    {
        Kind = kind; CustomerId = customerId; Customer = customer; Topic = topic;
        if (customer is not null)
        {
            var f = customer.Fields; DisplayName = f.DisplayName; ContactPerson = f.ContactPerson ?? ""; Phone = f.Phone ?? "";
            Email = f.Email ?? ""; Source = f.Source ?? ""; OriginalNote = f.OriginalNote ?? ""; IsSynthetic = customer.IsSynthetic;
        }
        if (topic is not null)
        {
            Need = topic.Fields.Need ?? ""; State = topic.Fields.State;
            foreach (var p in Products) p.Selected = topic.Fields.Products.Contains(p.Name);
            var plan = topic.Fields.NextContact;
            DateMode = plan is null ? "none" : plan.Date is null ? "text" : "exact";
            NextDate = plan?.Date?.ToDateTime(TimeOnly.MinValue); NextDescription = plan?.Description ?? ""; NextAction = plan?.Action ?? "";
        }
        ExpectedVersion = kind == EditorKind.Customer ? customer?.Version ?? 0 : topic?.Version ?? 0;
        foreach (var p in Products) p.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Dirty));
        initial = Snapshot();
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(DateMode) or nameof(State))
        { OnPropertyChanged(nameof(HasPlan)); OnPropertyChanged(nameof(ExactDate)); OnPropertyChanged(nameof(DescriptiveDate)); }
        if (e.PropertyName is nameof(IsSaving) or nameof(IsUncertain)) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanClose)); }
        if (e.PropertyName != nameof(Dirty)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Dirty)));
    }
    private string Snapshot() => JsonSerializer.Serialize(new { DisplayName, ContactPerson, Phone, Email, Source, OriginalNote, IsSynthetic,
        Need, Note, State, DateMode, NextDate, NextDescription, NextAction, Products = Products.Where(p => p.Selected).Select(p => p.Name) });
    private ContactPlan? Plan() => State == "closed" || DateMode == "none" ? null : new(DateMode == "exact" && NextDate is not null ? DateOnly.FromDateTime(NextDate.Value) : null,
        DateMode == "text" ? NextDescription : null, NextAction);
    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        if (IsSaving || HasConflict) return false;
        ClearErrors();
        IsLifecycleRejected = false;
        if (!IsCustomer && State != "closed" && DateMode == "exact" && NextDate is null)
        { SetError(nameof(NextDate), ["Wybierz datę kontaktu."]); Status = "Sprawdź formularz."; return false; }
        var wasUncertain = IsUncertain;
        IsSaving = true; Status = "Zapisywanie…";
        try
        {
            if (IsCustomer)
            {
                var fields = new CustomerFields(DisplayName, ContactPerson, Phone, Email, Source, OriginalNote);
                pending ??= Customer is null ? new CreateCustomer(requestId, fields, IsSynthetic) : new EditCustomer(requestId, ExpectedVersion, fields);
                Saved = await api.Save<SaveCustomerResult>(Customer is null ? HttpMethod.Post : HttpMethod.Put, Customer is null ? "customers" : $"customers/{Customer.Id}", pending, ct);
            }
            else
            {
                var fields = new TopicFields(Products.Where(p => p.Selected).Select(p => p.Name).ToArray(), Need, State, Plan());
                pending ??= IsConversation ? new RecordContact(requestId, ExpectedVersion, Note, State, Plan()) : Topic is null ? new CreateTopic(requestId, fields) : new EditTopic(requestId, ExpectedVersion, fields);
                Saved = await api.Save<TopicDto>(IsConversation || Topic is null ? HttpMethod.Post : HttpMethod.Put,
                    IsConversation ? $"topics/{Topic!.Id}/contacts" : Topic is null ? $"customers/{CustomerId}/topics" : $"topics/{Topic.Id}", pending, ct);
            }
            IsUncertain = false; initial = Snapshot(); Status = "Zapisano."; return true;
        }
        catch (ApiFailure e)
        {
            Status = e.Message + (e.Errors.Count == 0 ? "" : "\n" + string.Join("\n", e.Errors.SelectMany(x => x.Value)));
            foreach (var (key, values) in e.Errors) SetError(key switch
            { "displayName" => nameof(DisplayName), "contactPerson" => nameof(ContactPerson), "phone" => nameof(Phone), "email" => nameof(Email), "source" => nameof(Source), "originalNote" => nameof(OriginalNote),
                "need" => nameof(Need), "note" => nameof(Note), "state" => nameof(State), "products" => nameof(Products), "nextContact" => DateMode == "exact" ? nameof(NextDate) : nameof(NextDescription), _ => "Form" }, values);
            if (e.Status is 409 or 410)
            {
                IsUncertain = false;
                IsLifecycleRejected = e.Status == 410 || e.Message.Contains("Klient został usunięty.", StringComparison.Ordinal);
                HasConflict = e.Status == 409 && e.Message.StartsWith("Dane zostały zmienione.", StringComparison.Ordinal)
                    && (IsCustomer ? Customer is not null : Topic is not null);
                if (!HasConflict)
                {
                    pending = null; requestId = Guid.NewGuid();
                    Status += "\nFormularz zachowany. " + (IsLifecycleRejected ? "Przywróć klienta przed ponownym zapisem." : "Sprawdź dane i spróbuj ponownie.");
                }
            }
            else if (e.Status >= 500) IsUncertain = true;
            else if (!wasUncertain) { pending = null; IsUncertain = false; }
            if (e.Status == 401) throw;
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        { IsUncertain = true; Status = "Nie można potwierdzić zapisu. Formularz zachowany. Ponów ten sam zapis po odzyskaniu połączenia."; return false; }
        finally { IsSaving = false; }
    }
    public async Task LoadComparison(CrmClient api, CancellationToken ct)
    {
        if (!HasConflict) return;
        if (IsCustomer && Customer is null || !IsCustomer && Topic is null)
        { HasConflict = false; Status = "Ten formularz nie ma zapisanej wersji do porównania. Wpisane dane zachowano."; return; }
        latest = await api.Get<CustomerDetail>($"customers/{(IsCustomer ? Customer!.Id : CustomerId)}", ct);
        if (IsCustomer) { Comparison = $"Zapisane dane (wersja {latest.Customer.Version}):\n{Format.Customer(latest.Customer)}"; return; }
        var current = latest.Topics.FirstOrDefault(t => t.Id == Topic!.Id);
        Comparison = current is null ? "Temat nie jest już dostępny. Wpisane dane zachowano; wróć do klienta i odśwież kartę." :
            "Aktualnie zapisany temat:\n" + Format.Topic(current);
    }
    public void AcceptComparedVersion()
    {
        if (latest is null || !HasConflict) return;
        var current = IsCustomer ? null : latest.Topics.FirstOrDefault(t => t.Id == Topic?.Id);
        if (!IsCustomer && current is null) { Status = "Temat nie jest już dostępny. Formularz zachowano."; return; }
        ExpectedVersion = IsCustomer ? latest.Customer.Version : current!.Version;
        HasConflict = false; pending = null; requestId = Guid.NewGuid(); latest = null;
        Status = "Twoje wartości zachowano. Sprawdź je i wybierz Zapisz.";
    }
    private void SetError(string field, string[] values) { errors[field] = values; ErrorsChanged?.Invoke(this, new(field)); OnPropertyChanged(nameof(HasErrors)); }
    private void ClearErrors() { var keys = errors.Keys.ToArray(); errors.Clear(); foreach (var key in keys) ErrorsChanged?.Invoke(this, new(key)); }
}
public sealed partial class ProductChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private bool selected;
}
public static class Format
{
    public static string Plan(ContactPlan? p) => p is null ? "Bez terminu" : $"{(p.Date is { } date ? date.ToString("dd.MM.yyyy") : p.Description + " — termin do doprecyzowania")}\n{p.Action}";
    public static string Customer(CustomerDto c) => $"{c.Fields.DisplayName}\n{c.Fields.ContactPerson}\n{c.Fields.Phone}\n{c.Fields.Email}\nSkąd kontakt? {c.Fields.Source}\nPierwotna notatka: {c.Fields.OriginalNote}";
    public static string Topic(TopicDto t) => $"{string.Join(" / ", t.Fields.Products)}\n{CrmVocabulary.States.GetValueOrDefault(t.Fields.State)}\n{t.Fields.Need}\nNastępny krok: {Plan(t.Fields.NextContact)}";
}
