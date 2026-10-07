using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;
public enum MainSection { Customers, Archived, Queue, Detail, Tetrapod, Orders, OrderQuestions, Modules, Materials, Operations, Templates, TemplateProjects, TemplateCatalog, ShiftReports, Insights, Lego, Production }
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CrmClient api;
    private int loadVersion;
    private int historyVersion;
    public MainViewModel(CrmClient api)
    {
        this.api = api;
        api.SessionChanged += SessionChanged;
        RefreshSession();
    }
    private void SessionChanged(object? sender, EventArgs e) => RefreshSession();
    public void Dispose() => api.SessionChanged -= SessionChanged;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string product = "";
    [ObservableProperty] private string state = "";
    [ObservableProperty] private string due = "";
    [ObservableProperty] private string queueGroup = "today";
    [ObservableProperty] private string pageTitle = "Klienci";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private string customerSummary = "";
    [ObservableProperty] private string topicSummary = "";
    [ObservableProperty] private string historyInfo = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool listFailed;
    [ObservableProperty] private bool historyBusy;
    [ObservableProperty] private bool historyLoaded;
    [ObservableProperty] private bool historyFailed;
    [ObservableProperty] private MainSection section = MainSection.Customers;
    public string UserName => api.Session?.Name ?? "";
    public bool CanOpenLegacy => api.Session?.Role is "biuro" or "technolog" or "ceo" or "produkcja";
    public bool HasCrmAccess => api.Session?.TeamId is { } team && team != Guid.Empty;
    public bool CanCalculate => api.Session?.Role is "biuro" or "technolog";
    public bool CanReadOrders => api.Session?.Role is "biuro" or "technolog" or "ceo";
    public bool IsOrdersVisible => CanReadOrders && Section == MainSection.Orders;
    [ObservableProperty] private bool supportsOrderResources;
    [ObservableProperty] private int? pendingQuestions;
    public string QuestionsNavTitle => api.Session?.Role == "biuro" && SupportsOrderResources ? "Pytania · " + (PendingQuestions?.ToString() ?? "…") : "Pytania";
    partial void OnPendingQuestionsChanged(int? value) => OnPropertyChanged(nameof(QuestionsNavTitle));
    public bool CanOpenOrderQuestions => SupportsOrderResources && api.Session?.Role is "biuro" or "technolog";
    public bool IsOrderQuestionsVisible => CanOpenOrderQuestions && Section == MainSection.OrderQuestions;
    partial void OnSupportsOrderResourcesChanged(bool value)
    { PendingQuestions = null; OnPropertyChanged(nameof(QuestionsNavTitle)); OnPropertyChanged(nameof(CanOpenOrderQuestions)); OnPropertyChanged(nameof(IsOrderQuestionsVisible)); }
    public bool CanReadInsights => api.Session?.Role is "biuro" or "technolog" or "ceo";
    public string InsightsTitle => api.Session?.Role == "biuro" ? "Historia usług" : "Raporty";
    public bool IsInsightsVisible => CanReadInsights && Section == MainSection.Insights;
    public bool CanManageCatalog => api.Session?.Role == "technolog";
    [ObservableProperty] private bool supportsShiftReports;
    public bool CanReadShiftReports => SupportsShiftReports && api.Session?.Role is "produkcja" or "technolog" or "biuro" or "ceo";
    public bool IsShiftReportsVisible => CanReadShiftReports && Section == MainSection.ShiftReports;
    partial void OnSupportsShiftReportsChanged(bool value) { OnPropertyChanged(nameof(CanReadShiftReports)); OnPropertyChanged(nameof(IsShiftReportsVisible)); }
    [ObservableProperty] private bool supportsProduction;
    public bool CanReadProduction => SupportsProduction && api.Session?.Role is "produkcja" or "technolog" or "biuro" or "ceo";
    public bool IsProductionVisible => CanReadProduction && Section == MainSection.Production;
    partial void OnSupportsProductionChanged(bool value) { OnPropertyChanged(nameof(CanReadProduction)); OnPropertyChanged(nameof(IsProductionVisible)); }
    public bool CanAdminTemplates => api.Session?.Role == "technolog";
    public bool CanOfficeTemplates => api.Session?.Role is "biuro" or "ceo";
    public bool IsTemplatesVisible => CanAdminTemplates && Section is MainSection.Templates or MainSection.TemplateProjects || CanOfficeTemplates && Section == MainSection.TemplateCatalog;
    public bool IsMaterialsVisible => CanManageCatalog && Section == MainSection.Materials;
    public bool IsOperationsVisible => CanManageCatalog && Section == MainSection.Operations;
    public bool IsCrmVisible => HasCrmAccess && (Section is MainSection.Customers or MainSection.Archived or MainSection.Queue or MainSection.Detail);
    public bool IsListVisible => Section is MainSection.Customers or MainSection.Archived or MainSection.Queue;
    public bool IsDetailVisible => Section == MainSection.Detail;
    public bool IsLegoVisible => CanCalculate && Section == MainSection.Lego;
    public bool IsTetrapodVisible => Section == MainSection.Tetrapod;
    public bool IsModulesVisible => Section == MainSection.Modules;
    public bool IsCustomerActionVisible => HasCrmAccess && Section == MainSection.Customers;
    public bool IsClientsActive => (Section is MainSection.Customers or MainSection.Detail) && !Queue && !Archived;
    public bool IsQueueActive => Section == MainSection.Queue || Section == MainSection.Detail && Queue;
    public bool IsArchivedActive => Section == MainSection.Archived || Section == MainSection.Detail && Archived;
    public bool CanEditCustomer => HasCrmAccess && Detail is { Customer.ArchivedAt: null };
    public bool IsArchivedCustomer => Detail?.Customer.ArchivedAt is not null;
    public bool CanArchiveCustomer => CanEditCustomer;
    public bool CanRestoreCustomer => HasCrmAccess && IsArchivedCustomer;
    public bool CanAddTopic => CanEditCustomer;
    public bool CanActOnTopic => CanEditCustomer && SelectedTopic is not null && !HistoryBusy;
    partial void OnHistoryBusyChanged(bool value) => OnPropertyChanged(nameof(CanActOnTopic));
    public ObservableCollection<ListRow> Rows { get; } = [];
    public ObservableCollection<TopicChoice> Topics { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public CustomerDetail? Detail { get; private set; }
    public TopicDto? SelectedTopic { get; private set; }
    public int Page { get; set; } = 1;
    public int Total { get; private set; }
    public int TopicPage { get; private set; } = 1;
    public int TopicTotal { get; private set; }
    [ObservableProperty] private string topicPageInfo = "";
    public int HistoryPage { get; set; } = 1;
    public int HistoryTotal { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDescription))]
    private bool queue;
    partial void OnQueueChanged(bool value) { InvalidateRequests(); OnPropertyChanged(nameof(IsClientsActive)); OnPropertyChanged(nameof(IsQueueActive)); }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageDescription), nameof(IsArchivedActive), nameof(IsClientsActive))]
    private bool archived;
    partial void OnArchivedChanged(bool value) => InvalidateRequests();
    private void InvalidateRequests()
    {
        Interlocked.Increment(ref loadVersion);
        Interlocked.Increment(ref historyVersion);
        Busy = HistoryBusy = false;
    }
    partial void OnSectionChanged(MainSection value)
    {
        OnPropertyChanged(nameof(IsCrmVisible)); OnPropertyChanged(nameof(IsListVisible)); OnPropertyChanged(nameof(IsDetailVisible));
        OnPropertyChanged(nameof(IsLegoVisible)); OnPropertyChanged(nameof(IsTetrapodVisible)); OnPropertyChanged(nameof(IsModulesVisible));
        OnPropertyChanged(nameof(IsOrdersVisible)); OnPropertyChanged(nameof(IsOrderQuestionsVisible));
        OnPropertyChanged(nameof(IsMaterialsVisible)); OnPropertyChanged(nameof(IsOperationsVisible)); OnPropertyChanged(nameof(IsTemplatesVisible)); OnPropertyChanged(nameof(IsShiftReportsVisible));
        OnPropertyChanged(nameof(IsInsightsVisible));
        OnPropertyChanged(nameof(IsProductionVisible));
        OnPropertyChanged(nameof(IsCustomerActionVisible)); OnPropertyChanged(nameof(IsClientsActive)); OnPropertyChanged(nameof(IsQueueActive)); OnPropertyChanged(nameof(IsArchivedActive));
    }
    public string PageDescription => !HasCrmAccess ? "Wybierz moduł dostępny dla Twojego konta." : Archived ? "Usunięci klienci. Dane, tematy i historia są zachowane do przywrócenia." : Queue ? "Zaplanowane rozmowy i terminy do doprecyzowania." : "Dane kontaktowe, tematy rozmów i kolejne kroki.";
    public async Task Load(CancellationToken ct)
    {
        if (!HasCrmAccess)
        {
            Clear(); PageTitle = Section switch { MainSection.Insights when CanReadInsights => InsightsTitle, MainSection.Materials when CanManageCatalog => "Materiały", MainSection.Operations when CanManageCatalog => "Operacje i usługi", MainSection.Orders when CanReadOrders => "Zlecenia", MainSection.Lego when CanCalculate => "Kalkulator LEGO", MainSection.ShiftReports when CanReadShiftReports => "Raporty zmianowe", MainSection.Production when CanReadProduction => "Produkcja Tetrapodów", MainSection.Templates or MainSection.TemplateProjects or MainSection.TemplateCatalog when IsTemplatesVisible => TemplateTitle(Section), _ => CanCalculate ? "Tetrapod" : "Moduły" };
            PageInfo = ""; Status = ""; Busy = false; return;
        }
        var version = Interlocked.Increment(ref loadVersion);
        Interlocked.Increment(ref historyVersion);
        HistoryBusy = false;
        var page = Page;
        var isQueue = Queue;
        var isArchived = Archived;
        Busy = true; ListFailed = false; Status = "Wczytywanie…"; PageInfo = ""; Rows.Clear();
        try
        {
            ListRow[] rows;
            int total;
            if (isQueue)
            {
                var result = await api.Get<Page<QueueRow>>($"queue?group={QueueGroup}&page={page}", ct);
                rows = result.Items.Select(r => new ListRow(r.Customer, [r.Topic], null)).ToArray(); total = result.Total;
            }
            else if (isArchived)
            {
                var result = await api.Get<Page<CustomerDto>>($"customers/archived?q={Uri.EscapeDataString(Search)}&page={page}&pageSize=50", ct);
                rows = result.Items.Select(ListRow.FromArchived).ToArray(); total = result.Total;
            }
            else
            {
                string E(string value) => Uri.EscapeDataString(value);
                var result = await api.Get<Page<CustomerSummaryRow>>($"customers/summaries?q={E(Search)}&product={E(Product)}&state={E(State)}&due={E(Due)}&page={page}&pageSize=50", ct);
                rows = result.Items.Select(ListRow.FromSummary).ToArray(); total = result.Total;
            }
            ct.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref loadVersion)) throw new OperationCanceledException(ct);
            Rows.Clear(); foreach (var row in rows) Rows.Add(row);
            Total = total; PageInfo = $"Strona {page} · {total} {(isQueue ? "kontaktów" : "klientów")}";
            Status = total == 0 ? "Nie ma pozycji pasujących do filtrów." : "Dane aktualne.";
        }
        finally { if (version == Volatile.Read(ref loadVersion)) Busy = false; }
    }
    public void ListError(string message) { ListFailed = true; Status = message; }
    public async Task Open(Guid id, Guid? topicId, CancellationToken ct)
    {
        if (!HasCrmAccess) return;
        Interlocked.Increment(ref loadVersion);
        Busy = false;
        var version = Interlocked.Increment(ref historyVersion);
        HistoryBusy = true;
        try
        {
            var customer = await api.Get<CustomerDto>($"customers/{id}/record", ct);
            CheckDetailRequest(version, ct);
            var topics = await api.Get<Page<TopicDto>>($"customers/{id}/topics?page=1&pageSize=50", ct);
            CheckDetailRequest(version, ct);
            var topic = topicId is { } requested ? topics.Items.FirstOrDefault(t => t.Id == requested) : topics.Items.FirstOrDefault();
            if (topic is null && topicId is { } selectedId)
                topic = await api.Get<TopicDto>($"topics/{selectedId}", ct);
            if (customer.Id != id || topics.Items.Any(t => t.CustomerId != id) || topic is not null && topic.CustomerId != id)
                throw new ApiFailure(502, "Serwer zwrócił temat innego klienta.");
            CheckDetailRequest(version, ct);
            var history = topic is null ? null : await api.Get<Page<ContactEventDto>>($"topics/{topic.Id}/history?page=1", ct);
            CheckDetailRequest(version, ct);
            Detail = new(customer, topics.Items);
            PageTitle = customer.Fields.DisplayName;
            CustomerSummary = Format.Customer(customer) + (customer.ArchivedAt is null ? "" : "\nArchiwum — tylko do odczytu");
            ApplyTopics(topics, topic);
            ApplySelection(topic, history, 1);
            NotifyCustomerActions();
        }
        finally { if (version == Volatile.Read(ref historyVersion)) HistoryBusy = false; }
    }
    public async Task LoadTopicPage(int page, CancellationToken ct)
    {
        if (!HasCrmAccess || Detail is null || page < 1) return;
        var customer = Detail.Customer;
        var version = Interlocked.Increment(ref historyVersion);
        HistoryBusy = true;
        try
        {
            var topics = await api.Get<Page<TopicDto>>($"customers/{customer.Id}/topics?page={page}&pageSize=50", ct);
            CheckDetailRequest(version, ct);
            if (topics.Items.Any(t => t.CustomerId != customer.Id)) throw new ApiFailure(502, "Serwer zwrócił temat innego klienta.");
            var topic = topics.Items.FirstOrDefault();
            var history = topic is null ? null : await api.Get<Page<ContactEventDto>>($"topics/{topic.Id}/history?page=1", ct);
            CheckDetailRequest(version, ct);
            Detail = new(customer, topics.Items);
            ApplyTopics(topics, topic);
            ApplySelection(topic, history, 1);
        }
        finally { if (version == Volatile.Read(ref historyVersion)) HistoryBusy = false; }
    }
    private void ApplyTopics(Page<TopicDto> page, TopicDto? selected)
    {
        Topics.Clear();
        foreach (var topic in page.Items) Topics.Add(new(topic));
        if (selected is not null && page.Items.All(t => t.Id != selected.Id)) Topics.Add(new(selected));
        TopicPage = page.PageNumber;
        TopicTotal = page.Total;
        TopicPageInfo = $"Tematy: strona {TopicPage} · {TopicTotal} łącznie" +
            (selected is not null && page.Items.All(t => t.Id != selected.Id) ? " · wybrany temat spoza strony" : "");
        OnPropertyChanged(nameof(TopicPage)); OnPropertyChanged(nameof(TopicTotal)); OnPropertyChanged(nameof(Detail));
    }
    private void CheckDetailRequest(int version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref historyVersion)) throw new OperationCanceledException(ct);
    }
    public async Task SelectTopic(TopicDto? topic, CancellationToken ct)
    {
        if (!HasCrmAccess || Detail is null) return;
        if (topic is not null && topic.CustomerId != Detail.Customer.Id) throw new ArgumentException("Temat należy do innego klienta.", nameof(topic));
        var version = Interlocked.Increment(ref historyVersion);
        var page = HistoryPage;
        HistoryBusy = true; HistoryFailed = false; HistoryInfo = "Wczytywanie historii…";
        try
        {
            var result = topic is null ? null : await api.Get<Page<ContactEventDto>>($"topics/{topic.Id}/history?page={page}", ct);
            CheckDetailRequest(version, ct);
            ApplySelection(topic, result, page);
        }
        catch (OperationCanceledException) { if (version == Volatile.Read(ref historyVersion)) HistoryInfo = "Historia nie została zmieniona."; throw; }
        catch { if (version == Volatile.Read(ref historyVersion)) { HistoryFailed = true; HistoryInfo = "Nie udało się wczytać historii. Spróbuj ponownie."; } throw; }
        finally { if (version == Volatile.Read(ref historyVersion)) HistoryBusy = false; }
    }
    private void ApplySelection(TopicDto? topic, Page<ContactEventDto>? history, int page)
    {
        SelectedTopic = topic;
        OnPropertyChanged(nameof(SelectedTopic)); OnPropertyChanged(nameof(CanActOnTopic));
        TopicSummary = topic is null ? (Detail?.Customer.ArchivedAt is null ? "Brak tematów. Możesz zapisać dane klienta i dodać temat później." : "Brak tematów. Klient w archiwum — tylko do odczytu.") : Format.Topic(topic);
        HistoryPage = page;
        ApplyHistory(history);
    }
    private void NotifyCustomerActions()
    {
        OnPropertyChanged(nameof(CanEditCustomer)); OnPropertyChanged(nameof(CanAddTopic)); OnPropertyChanged(nameof(CanActOnTopic));
        OnPropertyChanged(nameof(IsArchivedCustomer)); OnPropertyChanged(nameof(CanArchiveCustomer)); OnPropertyChanged(nameof(CanRestoreCustomer));
    }
    private void ApplyHistory(Page<ContactEventDto>? result)
    {
        History.Clear(); if (result is not null) foreach (var entry in result.Items) History.Add(new(entry));
        HistoryTotal = result?.Total ?? 0; HistoryInfo = result is null ? "" : $"Strona {HistoryPage} · {result.Total} wpisów";
        HistoryFailed = false; HistoryLoaded = result is not null;
    }
    public void RefreshSession()
    {
        OnPropertyChanged(nameof(UserName)); OnPropertyChanged(nameof(CanOpenLegacy));
        OnPropertyChanged(nameof(HasCrmAccess)); OnPropertyChanged(nameof(CanCalculate)); OnPropertyChanged(nameof(IsLegoVisible)); OnPropertyChanged(nameof(IsCrmVisible));
        OnPropertyChanged(nameof(CanReadOrders)); OnPropertyChanged(nameof(IsOrdersVisible));
        OnPropertyChanged(nameof(CanOpenOrderQuestions)); OnPropertyChanged(nameof(IsOrderQuestionsVisible));
        OnPropertyChanged(nameof(CanReadShiftReports)); OnPropertyChanged(nameof(CanAdminTemplates)); OnPropertyChanged(nameof(CanOfficeTemplates)); OnPropertyChanged(nameof(CanManageCatalog)); OnPropertyChanged(nameof(IsMaterialsVisible)); OnPropertyChanged(nameof(IsOperationsVisible)); OnPropertyChanged(nameof(IsTemplatesVisible)); OnPropertyChanged(nameof(IsShiftReportsVisible));
        PendingQuestions = null; OnPropertyChanged(nameof(QuestionsNavTitle));
        OnPropertyChanged(nameof(CanReadInsights)); OnPropertyChanged(nameof(InsightsTitle)); OnPropertyChanged(nameof(IsInsightsVisible));
        OnPropertyChanged(nameof(CanReadProduction)); OnPropertyChanged(nameof(IsProductionVisible));
        OnPropertyChanged(nameof(IsCustomerActionVisible));
        OnPropertyChanged(nameof(PageDescription)); NotifyCustomerActions();
        if (!HasCrmAccess)
        {
            var keepInsights = Section == MainSection.Insights && CanReadInsights;
            var keepOrders = Section == MainSection.Orders && CanReadOrders;
            var keepQuestions = Section == MainSection.OrderQuestions && CanOpenOrderQuestions;
            var keepCatalog = CanManageCatalog && Section is MainSection.Materials or MainSection.Operations;
            var keepTemplates = IsTemplatesVisible;
            var keepShiftReports = IsShiftReportsVisible;
            var keepProduction = IsProductionVisible;
            var keepLego = IsLegoVisible;
            var catalogSection = Section;
            Clear(); Section = keepInsights ? MainSection.Insights : keepProduction ? MainSection.Production : keepShiftReports ? MainSection.ShiftReports : keepLego ? MainSection.Lego : keepTemplates ? catalogSection : keepQuestions ? MainSection.OrderQuestions : keepCatalog ? catalogSection : keepOrders ? MainSection.Orders : CanCalculate ? MainSection.Tetrapod : MainSection.Modules;
            PageTitle = keepInsights ? InsightsTitle : keepProduction ? "Produkcja Tetrapodów" : keepShiftReports ? "Raporty zmianowe" : keepLego ? "Kalkulator LEGO" : keepTemplates ? TemplateTitle(Section) : keepQuestions ? "Pytania od Technologa" : keepCatalog ? Section == MainSection.Materials ? "Materiały" : "Operacje i usługi" : keepOrders ? "Zlecenia" : CanCalculate ? "Tetrapod" : "Moduły"; PageInfo = ""; Status = "";
        }
        else if (Section == MainSection.Lego && !CanCalculate || Section == MainSection.Insights && !CanReadInsights || Section == MainSection.ShiftReports && !CanReadShiftReports || Section == MainSection.Production && !CanReadProduction || (Section is MainSection.Templates or MainSection.TemplateProjects or MainSection.TemplateCatalog) && !IsTemplatesVisible || Section == MainSection.Modules || !CanManageCatalog && Section is MainSection.Materials or MainSection.Operations)
        { Section = MainSection.Customers; PageTitle = "Klienci"; }
    }
    public static string TemplateTitle(MainSection section) => section switch { MainSection.Templates => "Szablony / SOP", MainSection.TemplateProjects => "Projekty", _ => "Katalog produktów" };
    public void Clear()
    {
        Search = Product = State = Due = ""; QueueGroup = "today"; Queue = Archived = false;
        Interlocked.Increment(ref loadVersion);
        Interlocked.Increment(ref historyVersion);
        Busy = false; Rows.Clear(); Topics.Clear(); History.Clear(); Detail = null; SelectedTopic = null; OnPropertyChanged(nameof(CanActOnTopic)); CustomerSummary = ""; TopicSummary = "";
        HistoryLoaded = HistoryFailed = HistoryBusy = ListFailed = false;
        Page = TopicPage = HistoryPage = 1; Total = TopicTotal = HistoryTotal = 0;
        PageInfo = TopicPageInfo = HistoryInfo = "";
        OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(SelectedTopic));
        OnPropertyChanged(nameof(TopicPage)); OnPropertyChanged(nameof(TopicTotal)); NotifyCustomerActions();
    }
}
public sealed record ListRow(CustomerDto Customer, TopicDto[] Topics, DateTimeOffset? LastContact)
{
    public TopicSummary[] TopicPreviews { get; init; } = [];
    public int TopicCount { get; init; } = Topics.Length;
    public bool IsSummary { get; init; }
    public bool IsArchived { get; init; }
    public static ListRow FromSummary(CustomerSummaryRow row) => new(row.Customer, [], row.LastContact)
        { TopicPreviews = row.Topics, TopicCount = row.TopicCount, IsSummary = true };
    public static ListRow FromArchived(CustomerDto customer) => new(customer, [], null) { IsArchived = true };
    public string Name => Customer.Fields.DisplayName + (Customer.IsSynthetic ? " · dane fikcyjne" : "");
    public string Contact => string.Join(" · ", new[] { Customer.Fields.ContactPerson, Customer.Fields.Phone, Customer.Fields.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
    private string PreviewLabel => $"Podgląd {TopicPreviews.Length} z {TopicCount} tematów";
    public string Products => IsArchived ? "Archiwum" : IsSummary ? PreviewLabel + ": " + string.Join(" / ", TopicPreviews.SelectMany(t => t.Products).Distinct()) : string.Join(" / ", Topics.SelectMany(t => t.Fields.Products).Distinct());
    public string States => IsArchived ? "Tylko do odczytu" : IsSummary ? "Stany w podglądzie: " + string.Join("; ", TopicPreviews.Select(t => CrmVocabulary.States.GetValueOrDefault(t.State)).Distinct()) : string.Join("; ", Topics.Select(t => CrmVocabulary.States.GetValueOrDefault(t.Fields.State)).Distinct());
    public string Last => LastContact is { } date ? TimeZoneInfo.ConvertTime(date, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).ToString("dd.MM.yyyy HH:mm") : "—";
    public string Preview => $"{Name}\n{Contact}\n{Products}\n{States}\n{Next}";
    public string Next => IsArchived ? "Otwórz klienta, aby zobaczyć zachowane tematy i plany." : IsSummary ? TopicCount == 0 ? "Brak tematów" : "Terminy — " + PreviewLabel.ToLowerInvariant() + ": " + string.Join("; ", TopicPreviews.Select(t => Format.Plan(t.NextContact).Replace('\n', ' '))) : Topics.Length == 0 ? "Bez terminu" : string.Join("; ", Topics.Select(t => Format.Plan(t.Fields.NextContact).Replace('\n', ' ')));
}
public sealed record TopicChoice(TopicDto Topic)
{
    public string Label => string.Join(" / ", Topic.Fields.Products) + (Topic.Fields.State == "closed" ? " · zamknięty" : "");
}
public sealed record HistoryRow(ContactEventDto Event)
{
    public string Heading => $"{TimeZoneInfo.ConvertTime(Event.RecordedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")):dd.MM.yyyy HH:mm} · {Event.ActorName} · {Event.Kind switch { "conversation" => "Zapis rozmowy / próby kontaktu", "changed" => "Zmiana tematu / planu", _ => "Dodano temat" }}";
    public string Text => $"{CrmVocabulary.States.GetValueOrDefault(Event.State)}\n{Event.Note}\n{Format.Plan(Event.NextContact)}";
}
