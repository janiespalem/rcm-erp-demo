using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Rcm.Contracts;

namespace Rcm.Desktop;
public partial class MainWindow : Window
{
    private readonly CrmClient api;
    public MainViewModel Model { get; }
    public BackgroundUpdates Updates { get; }
    private readonly IBackgroundUpdateClient updateClient;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer restartTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset lastActivity = DateTimeOffset.UtcNow;
    private Point lastPointer;
    private DateTimeOffset? restartAt;
    private DateTimeOffset postponeUntil;
    private CancellationTokenSource loading = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource questionCount = new();
    private int questionCountVersion;
    private bool ready;
    private bool selecting;
    private MainSection lastCrmSection = MainSection.Customers;
    private bool lastCrmQueue;
    private bool lastCrmArchived;
    public MainWindow(CrmClient api, ILegoCartStore? legoCartStore = null, IBackgroundUpdateClient? updateClient = null)
    {
        this.api = api; this.updateClient = updateClient ?? new BackgroundUpdateClient(CanRestartForUpdate);
        Updates = new(this.updateClient); Model = new(api); DataContext = Model; InitializeComponent();
        updateTimer.Tick += async (_, _) => await Updates.CheckAndDownload(lifetime.Token);
        restartTimer.Tick += async (_, _) => await RestartWhenIdle();
        PreviewMouseDown += (_, _) => Activity();
        PreviewMouseMove += (_, _) =>
        {
            var pointer = Mouse.GetPosition(this);
            if (pointer == lastPointer) return;
            lastPointer = pointer;
            Activity();
        };
        PreviewMouseWheel += (_, _) => Activity();
        PreviewKeyDown += (_, _) => Activity();
        ProductFilter.ItemsSource = Options("Wszystkie produkty", CrmVocabulary.Products.ToDictionary(x => x, x => x));
        StateFilter.ItemsSource = Options("Wszystkie stany", CrmVocabulary.States); DueFilter.ItemsSource = Options("Wszystkie terminy", CrmVocabulary.DueGroups);
        QueueGroups.ItemsSource = CrmVocabulary.DueGroups.Take(3).ToArray();
        Tetrapod.SetClient(api); LegoWorkspace.SetClient(api, legoCartStore);
        TemplatesWorkspace.SetClient(api); ShiftReportsWorkspace.SetClient(api);
        ProductionWorkspace.SetClient(api);
        OrderWorkspace.SetClient(api);
        InsightsWorkspace.SetClient(api);
        Model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Model.Section) && Model.Section != MainSection.Insights) InsightsWorkspace.CancelRead(); };
        OrderQuestionWorkspace.SetClient(api);
        OrderQuestionWorkspace.Model.PropertyChanged += async (_, e) => { if (e.PropertyName == nameof(OrderQuestionWorkspace.Model.Busy) && !OrderQuestionWorkspace.Model.Busy) await RefreshPendingQuestions(); };

        MaterialCatalog.SetClient(api, CatalogKind.Materials); OperationCatalog.SetClient(api, CatalogKind.Operations);
        api.SessionChanged += QuestionsSessionChanged;
        ready = true; Loaded += async (_, _) => { lastPointer = Mouse.GetPosition(this); updateTimer.Start(); restartTimer.Start(); if (Model.HasCrmAccess) Search.Focus(); else if (Model.CanCalculate) Tetrapod.FocusInput(); else LegacyModules.Focus(); await Task.WhenAll(RefreshOrderFeatures(), RefreshShiftFeatures(), RefreshProductionFeatures(), Reload()); };
        Closing += (_, e) =>
        {
            if (ActivateOwnedForm()) e.Cancel = true;
            else if (OrderWorkspace.IsSaving) { e.Cancel = true; OrderWorkspace.Model.Status = "Operacja trwa. Poczekaj na potwierdzenie."; }
            else if (LegoWorkspace.IsWorking) { e.Cancel = true; LegoWorkspace.Model.Status = "Operacja LEGO trwa. Poczekaj lub zatrzymaj oczekiwanie."; }
            else if (LegoWorkspace.Dirty && !LegoWorkspace.ConfirmLeave()) e.Cancel = true;
            else if (Tetrapod.Model.IsCalculating) { e.Cancel = true; Tetrapod.Model.Status = "Obliczanie trwa. Poczekaj lub zatrzymaj oczekiwanie."; }
            else if (Tetrapod.Model.Dirty && !ConfirmDiscard()) e.Cancel = true;
        };
        Closed += (_, _) => { ready = false; updateTimer.Stop(); restartTimer.Stop(); api.SessionChanged -= QuestionsSessionChanged; lifetime.Cancel(); this.updateClient.Dispose(); loading.Cancel(); questionCount.Cancel(); InsightsWorkspace.Dispose(); OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead(); MaterialCatalog.CancelRead(); OperationCatalog.CancelRead(); TemplatesWorkspace.CancelRead(); ShiftReportsWorkspace.CancelRead(); ProductionWorkspace.CancelRead(); LegoWorkspace.Dispose(); Tetrapod.CancelCalculation(); Model.Dispose(); api.Dispose(); };
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAndDownload(lifetime.Token);
    private bool CanRestartForUpdate() => ready && !OwnedWindows.Cast<Window>().Any() && !OrderWorkspace.IsSaving
        && !LegoWorkspace.IsWorking && !LegoWorkspace.Dirty && !Tetrapod.Model.IsCalculating && !Tetrapod.Model.Dirty;
    private void Activity()
    {
        lastActivity = DateTimeOffset.UtcNow;
        if (restartAt is null) return;
        restartAt = null; Updates.RestartPending = false;
        if (Updates.Ready && !Updates.Busy) Updates.Status = $"Aktualizacja {Updates.Version} gotowa. Instalacja po zakończeniu pracy.";
    }
    private void PostponeUpdate_Click(object sender, RoutedEventArgs e)
    {
        Activity(); postponeUntil = DateTimeOffset.UtcNow.AddHours(1);
        Updates.Status = "Automatyczna instalacja odłożona o godzinę.";
    }
    private async Task RestartWhenIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var safe = CanRestartForUpdate();
        if (!safe) lastActivity = now;
        if (!Updates.Ready || Updates.Busy || !api.RememberMe || !IsVisible || !safe
            || now < postponeUntil || now - lastActivity < TimeSpan.FromMinutes(2))
        {
            if (restartAt is not null) { restartAt = null; Updates.RestartPending = false; if (Updates.Ready && !Updates.Busy) Updates.Status = $"Aktualizacja {Updates.Version} gotowa. Instalacja po zakończeniu pracy."; }
            return;
        }
        restartAt ??= now.AddSeconds(30);
        Updates.RestartPending = true;
        Updates.Status = $"Aktualizacja {Updates.Version}. Restart za {Math.Max(0, (int)Math.Ceiling((restartAt.Value - now).TotalSeconds))} s.";
        if (now >= restartAt) await InstallReadyUpdate();
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!Updates.Ready || Updates.Busy) return;
        if (!OwnedWindows.Cast<Window>().Any() && !OrderWorkspace.IsSaving && !LegoWorkspace.IsWorking && !Tetrapod.Model.IsCalculating)
        {
            if (LegoWorkspace.Dirty) { if (!LegoWorkspace.ConfirmLeave()) return; LegoWorkspace.Clear(); }
            if (Tetrapod.Model.Dirty) { if (!ConfirmDiscard()) return; Tetrapod.Clear(); }
        }
        await InstallReadyUpdate();
    }
    private async Task InstallReadyUpdate()
    {
        if (!Updates.Ready || Updates.Busy) return;
        var safe = CanRestartForUpdate();
        if (!safe) { ActivateOwnedForm(); await Updates.ApplyIfSafe(false, lifetime.Token); return; }
        restartAt = null; Updates.RestartPending = false;
        IsEnabled = false;
        try { await Updates.ApplyIfSafe(true, lifetime.Token); }
        finally { if (ready) IsEnabled = true; }
    }
    private bool ActivateOwnedForm()
    {
        var form = OwnedWindows.Cast<Window>().FirstOrDefault(w => w is EditorWindow or CustomerLifecycleWindow or OrderEditorWindow or OrderQuoteWindow or OrderResourcesWindow or OrderAttachmentsWindow or OrderTemplateWindow or OrderDocumentsWindow or CatalogEditorWindow or TemplateEditorWindow or ShiftReportsEditorWindow or ProductionContractWindow or SteelDeliveryWindow);
        if (form is null) return false;
        form.Activate(); return true;
    }
    private bool ConfirmDiscard() => MessageBox.Show(this, "Odrzucić wpisane wartości Tetrapod i niezapisane zmiany dostaw? Zapisana historia dostaw pozostanie na tym komputerze.",
        "Dane kalkulatora", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private static KeyValuePair<string, string>[] Options(string all, IEnumerable<KeyValuePair<string, string>> values) => new[] { new KeyValuePair<string, string>("", all) }.Concat(values).ToArray();
    private async Task Safely(Func<Task> action, CancellationToken ct = default, bool list = false)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("Upłynął czas oczekiwania. Spróbuj ponownie.", list); }
        catch (ApiFailure ex)
        {
            if (ct.IsCancellationRequested) return;
            Fail(ex.Status == 403 ? "Brak dostępu do tych danych." : ex.Message, list);
            if (ex.Status == 401 && !lifetime.IsCancellationRequested && new LoginWindow(api) { Owner = this }.ShowDialog() == true)
            {
                if (ct.IsCancellationRequested) return;
                try { await action(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception retry) when (retry is ApiFailure or HttpRequestException or OperationCanceledException) { Fail(retry is OperationCanceledException ? "Upłynął czas oczekiwania. Spróbuj ponownie." : retry.Message, list); }
            }
        }
        catch (HttpRequestException) { if (!ct.IsCancellationRequested) Fail("Brak połączenia. Sprawdź internet i spróbuj ponownie.", list); }
    }
    private void Fail(string message, bool list) { if (list) Model.ListError(message); else Model.Status = message; }
    private CancellationToken BeginLoad()
    {
        loading.Cancel(); loading.Dispose(); loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        return loading.Token;
    }
    private async Task Reload(bool debounce = false)
    {
        if (!ready) return;
        var ct = BeginLoad();
        await Safely(async () => { if (debounce) await Task.Delay(300, ct); await Model.Load(ct); }, ct, true);
    }
    private async void Search_Changed(object sender, TextChangedEventArgs e) { Model.Page = 1; await Reload(true); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { Model.Page = 1; await Reload(); }
    private async void Clients_Click(object sender, RoutedEventArgs e)
    {
        OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        if (!Model.HasCrmAccess) return;
        if (RestoreDetail(false, false)) return;
        Model.Queue = false; Model.Archived = false; await ShowList();
    }
    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        if (!Model.HasCrmAccess) return;
        if (RestoreDetail(true, false)) return;
        Model.Archived = false; Model.Queue = true; await ShowList();
    }
    private async void Archived_Click(object sender, RoutedEventArgs e)
    {
        OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        if (!Model.HasCrmAccess) return;
        if (RestoreDetail(false, true)) return;
        Model.Queue = false; Model.Archived = true; await ShowList();
    }
    private bool RestoreDetail(bool queue, bool archived)
    {
        if (Model.Section != MainSection.Tetrapod || lastCrmSection != MainSection.Detail || lastCrmQueue != queue || lastCrmArchived != archived || Model.Detail is null) return false;
        Model.Queue = queue; Model.Archived = archived; Model.Section = MainSection.Detail; Model.PageTitle = Model.Detail.Customer.Fields.DisplayName;
        SyncTopicSelection();
        return true;
    }
    private async Task ShowList()
    {
        Model.Section = Model.Queue ? MainSection.Queue : Model.Archived ? MainSection.Archived : MainSection.Customers;
        Filters.Visibility = Model.Queue ? Visibility.Collapsed : Visibility.Visible; QueueGroups.Visibility = Model.Queue ? Visibility.Visible : Visibility.Collapsed;
        ProductFields.Visibility = StateFields.Visibility = DueFields.Visibility = Model.Archived ? Visibility.Collapsed : Visibility.Visible;
        Model.PageTitle = Model.Queue ? "Do kontaktu" : Model.Archived ? "Usunięci klienci" : "Klienci"; Model.Page = 1; await Reload();
    }
    private async void RetryList_Click(object sender, RoutedEventArgs e) => await Reload();
    private async void Back_Click(object sender, RoutedEventArgs e) => await ShowList();
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (Model.Page > 1) { Model.Page--; await Reload(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Model.Page * 50 < Model.Total) { Model.Page++; await Reload(); } }
    private async void Open_Click(object sender, RoutedEventArgs e) { if (Customers.SelectedItem is ListRow row) await Open(row.Customer.Id, Model.Queue ? row.Topics.FirstOrDefault()?.Id : null); }
    private void Customers_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Open_Click(sender, e); } }
    private async Task Open(Guid id, Guid? topicId = null)
    {
        var ct = BeginLoad();
        await Safely(async () =>
        {
            await Model.Open(id, topicId, ct);
            ct.ThrowIfCancellationRequested();
            Model.Section = MainSection.Detail; Model.Status = "Dane aktualne.";
        }, ct);
        if (ct == loading.Token) SyncTopicSelection();
    }
    private void SyncTopicSelection()
    {
        selecting = true;
        try { TopicSelector.SelectedItem = Model.Topics.FirstOrDefault(t => t.Topic.Id == Model.SelectedTopic?.Id); }
        finally { selecting = false; }
    }
    private async void Topic_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || TopicSelector.SelectedItem is not TopicChoice t) return;
        var ct = BeginLoad(); Model.HistoryPage = 1;
        await Safely(() => Model.SelectTopic(t.Topic, ct), ct);
        if (ct != loading.Token) return;
        if (Model.SelectedTopic?.Id != t.Topic.Id) SyncTopicSelection();
    }
    private async Task Edit(EditorViewModel model)
    {
        if (!Model.HasCrmAccess) return;
        loading.Cancel();
        var window = new EditorWindow(api, model) { Owner = this };
        if (window.ShowDialog() != true) return;
        if (model.Saved is SaveCustomerResult result) await Open(result.Customer.Id);
        else if (model.Saved is TopicDto topic) await Open(topic.CustomerId, topic.Id);
        Model.Status = "Zapisano.";
    }
    private async void AddCustomer_Click(object sender, RoutedEventArgs e) => await Edit(new(EditorKind.Customer));
    private async void EditCustomer_Click(object sender, RoutedEventArgs e) { if (Model.CanEditCustomer && Model.Detail is { } d) await Edit(new(EditorKind.Customer, d.Customer.Id, d.Customer)); }
    private async void AddTopic_Click(object sender, RoutedEventArgs e) { if (Model.CanAddTopic && Model.Detail is { } d) await Edit(new(EditorKind.Topic, d.Customer.Id)); }
    private async void EditTopic_Click(object sender, RoutedEventArgs e) { if (Model.CanActOnTopic && Model.SelectedTopic is { } t) await Edit(new(EditorKind.Topic, t.CustomerId, topic: t)); }
    private async void Conversation_Click(object sender, RoutedEventArgs e) { if (Model.CanActOnTopic && Model.SelectedTopic is { } t) await Edit(new(EditorKind.Conversation, t.CustomerId, topic: t)); }
    private async void ArchiveCustomer_Click(object sender, RoutedEventArgs e) { if (Model.CanArchiveCustomer) await ChangeLifecycle(false); }
    private async void RestoreCustomer_Click(object sender, RoutedEventArgs e) { if (Model.CanRestoreCustomer) await ChangeLifecycle(true); }
    private async Task ChangeLifecycle(bool restore)
    {
        if (Model.Detail is not { } detail) return;
        loading.Cancel();
        var model = new CustomerLifecycleViewModel(detail.Customer, restore);
        if (new CustomerLifecycleWindow(api, model) { Owner = this }.ShowDialog() != true) return;
        Model.Queue = Model.Archived = false;
        if (restore) await Open(detail.Customer.Id);
        else await ShowList();
        Model.Status = model.Status;
    }
    private async void TopicsPrevious_Click(object sender, RoutedEventArgs e) { if (Model.TopicPage > 1) await TopicPage(Model.TopicPage - 1); }
    private async void TopicsNext_Click(object sender, RoutedEventArgs e) { if (Model.TopicPage * 50 < Model.TopicTotal) await TopicPage(Model.TopicPage + 1); }
    private async Task TopicPage(int page)
    {
        var ct = BeginLoad();
        await Safely(() => Model.LoadTopicPage(page, ct), ct);
        if (ct == loading.Token) SyncTopicSelection();
    }
    private async void HistoryPrevious_Click(object sender, RoutedEventArgs e) { if (Model.HistoryPage > 1) { Model.HistoryPage--; await ReloadHistory(); } }
    private async void HistoryNext_Click(object sender, RoutedEventArgs e) { if (Model.HistoryPage * 50 < Model.HistoryTotal) { Model.HistoryPage++; await ReloadHistory(); } }
    private async void RetryHistory_Click(object sender, RoutedEventArgs e) => await ReloadHistory();
    private async Task ReloadHistory()
    {
        var ct = BeginLoad();
        await Safely(() => Model.SelectTopic(Model.SelectedTopic, ct), ct);
        if (ct == loading.Token) SyncTopicSelection();
    }
    private void Legacy_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanOpenLegacy) return;
        Model.Status = "W profilu demo dostępne są moduły aplikacji natywnej.";
    }
    private async void Lego_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanCalculate || ActivateOwnedForm()) return;
        loading.Cancel(); Model.Section = MainSection.Lego; Model.PageTitle = "Kalkulator LEGO"; await LegoWorkspace.Activate();
    }
    private void Tetrapod_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanCalculate) return;
        OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        if (Model.Section is MainSection.Customers or MainSection.Archived or MainSection.Queue or MainSection.Detail)
        { lastCrmSection = Model.Section; lastCrmQueue = Model.Queue; lastCrmArchived = Model.Archived; }
        loading.Cancel(); Model.Section = MainSection.Tetrapod; Model.PageTitle = "Tetrapod"; Tetrapod.FocusInput();
    }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (ActivateOwnedForm()) return;
        if (OrderWorkspace.IsSaving) { OrderWorkspace.Model.Status = "Operacja trwa. Poczekaj na potwierdzenie."; return; }
        if (LegoWorkspace.IsWorking) { LegoWorkspace.Model.Status = "Operacja LEGO trwa. Poczekaj lub zatrzymaj oczekiwanie."; return; }
        if (LegoWorkspace.Dirty && !LegoWorkspace.ConfirmLeave()) return;
        if (Tetrapod.Model.IsCalculating) { Tetrapod.Model.Status = "Obliczanie trwa. Poczekaj lub zatrzymaj oczekiwanie."; return; }
        if (Tetrapod.Model.Dirty && !ConfirmDiscard()) return;
        using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await api.LogoutAsync(logoutTimeout.Token); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { MessageBox.Show("Nie można usunąć zapamiętanego logowania z tego komputera. Spróbuj ponownie.", "FactoryFlow"); return; }
        ready = false; questionCount.Cancel(); Model.PendingQuestions = null; InsightsWorkspace.Clear(); loading.Cancel(); OrderWorkspace.Clear(); OrderQuestionWorkspace.Clear(); MaterialCatalog.Clear(); OperationCatalog.Clear(); TemplatesWorkspace.Clear(); ShiftReportsWorkspace.Clear(); ProductionWorkspace.Clear(); LegoWorkspace.Clear(); Model.SupportsShiftReports = false; Model.SupportsProduction = false; Model.SupportsOrderResources = false; Tetrapod.Clear(); Model.Clear(); lastCrmSection = MainSection.Customers; lastCrmQueue = lastCrmArchived = false; Hide();
        if (new LoginWindow(api).ShowDialog() != true) { Close(); return; }
        ready = true; Show(); Model.Queue = false; await RefreshOrderFeatures(); await RefreshShiftFeatures(); await RefreshProductionFeatures();
        if (Model.HasCrmAccess) await ShowList();
        else if (!Model.IsShiftReportsVisible) { Model.Section = Model.CanCalculate ? MainSection.Tetrapod : MainSection.Modules; Model.PageTitle = Model.CanCalculate ? "Tetrapod" : "Moduły"; }
    }
    private async Task RefreshOrderFeatures()
    {
        if (!Model.CanReadOrders) { Model.SupportsOrderResources = false; return; }
        await Safely(async () =>
        {
            try { Model.SupportsOrderResources = (await api.ReadOrders<OrderFeatures>("/features", lifetime.Token)).Resources; }
            catch (ApiFailure ex) when (ex.Status == 404) { Model.SupportsOrderResources = false; }
        }, lifetime.Token);
        await RefreshPendingQuestions();
    }
    private void QuestionsSessionChanged(object? sender, EventArgs e)
    { Interlocked.Increment(ref questionCountVersion); questionCount.Cancel(); Model.PendingQuestions = null; }
    private async Task RefreshPendingQuestions()
    {
        if (!ready || lifetime.IsCancellationRequested) return;
        var version = Interlocked.Increment(ref questionCountVersion);
        questionCount.Cancel(); questionCount.Dispose(); questionCount = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        questionCount.CancelAfter(TimeSpan.FromSeconds(10)); var ct = questionCount.Token; var user = api.Session?.UserId;
        if (!Model.SupportsOrderResources || api.Session?.Role != "biuro") { Model.PendingQuestions = null; return; }
        try
        {
            var result = await api.ReadOrders<Page<OrderQuestionQueueRow>>("/questions?status=pending&page=1&pageSize=1", ct);
            if (!ct.IsCancellationRequested && version == Volatile.Read(ref questionCountVersion) && user == api.Session?.UserId && api.Session?.Role == "biuro" && Model.SupportsOrderResources)
                Model.PendingQuestions = result.Total;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ApiFailure or HttpRequestException)
        { if (version == Volatile.Read(ref questionCountVersion)) Model.PendingQuestions = null; }
    }
    private async void OrderQuestions_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanOpenOrderQuestions) return;
        OrderWorkspace.CancelRead(); loading.Cancel();
        Model.Section = MainSection.OrderQuestions; Model.PageTitle = "Pytania od Technologa";
        await OrderQuestionWorkspace.Activate();
    }
    private async void Insights_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanReadInsights || ActivateOwnedForm()) return;
        loading.Cancel(); OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        Model.Section = MainSection.Insights; Model.PageTitle = Model.InsightsTitle;
        await InsightsWorkspace.Activate();
    }
    private async void Orders_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanReadOrders) return;
        OrderQuestionWorkspace.CancelRead(); loading.Cancel(); Model.Section = MainSection.Orders; Model.PageTitle = "Zlecenia";
        await OrderWorkspace.Activate();
        Model.SupportsOrderResources = OrderWorkspace.Model.SupportsResources;
        await RefreshPendingQuestions();
    }
    private async Task RefreshShiftFeatures()
    {
        if (api.Session?.Role is not ("produkcja" or "technolog" or "biuro" or "ceo")) { Model.SupportsShiftReports = false; return; }
        await Safely(async () =>
        {
            ShiftReportFeatures features;
            try { features = await api.ReadShiftReports<ShiftReportFeatures>("/features", lifetime.Token); }
            catch (ApiFailure ex) when (ex.Status == 404) { Model.SupportsShiftReports = false; return; }
            Model.SupportsShiftReports = features.Read;
            if (features.Read && api.Session?.Role == "produkcja" && Model.Section == MainSection.Modules)
            { Model.Section = MainSection.ShiftReports; Model.PageTitle = "Raporty zmianowe"; await ShiftReportsWorkspace.Activate(features); }
        }, lifetime.Token);
    }
    private async void ShiftReports_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanReadShiftReports || ActivateOwnedForm()) return;
        loading.Cancel(); Model.Section = MainSection.ShiftReports; Model.PageTitle = "Raporty zmianowe"; await ShiftReportsWorkspace.Activate();
    }
    private async Task RefreshProductionFeatures()
    {
        Model.SupportsProduction = false;
        if (api.Session is not { ProductionEnabled: true, Role: "produkcja" or "technolog" or "biuro" or "ceo" }) return;
        await Safely(async () =>
        {
            try { Model.SupportsProduction = (await api.ReadProduction<ProductionFeatures>("/features", lifetime.Token)).Read; }
            catch (ApiFailure ex) when (ex.Status == 404) { Model.SupportsProduction = false; }
        }, lifetime.Token);
    }
    private async void Production_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanReadProduction || ActivateOwnedForm()) return;
        loading.Cancel(); Model.Section = MainSection.Production; Model.PageTitle = "Produkcja Tetrapodów";
        await ProductionWorkspace.Activate();
    }
    private async void Templates_Click(object sender, RoutedEventArgs e) => await ShowTemplates(MainSection.Templates, TemplateWorkspaceMode.Sop);
    private async void Projects_Click(object sender, RoutedEventArgs e) => await ShowTemplates(MainSection.TemplateProjects, TemplateWorkspaceMode.Projects);
    private async void TemplateCatalog_Click(object sender, RoutedEventArgs e) => await ShowTemplates(MainSection.TemplateCatalog, TemplateWorkspaceMode.Catalog);
    private async Task ShowTemplates(MainSection section, TemplateWorkspaceMode mode)
    {
        if ((mode == TemplateWorkspaceMode.Catalog ? !Model.CanOfficeTemplates : !Model.CanAdminTemplates) || ActivateOwnedForm()) return;
        loading.Cancel(); OrderWorkspace.CancelRead(); OrderQuestionWorkspace.CancelRead();
        TemplatesWorkspace.SetMode(mode); Model.Section = section; Model.PageTitle = MainViewModel.TemplateTitle(section);
        await TemplatesWorkspace.Activate();
    }
    private async void Materials_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanManageCatalog || ActivateOwnedForm()) return;
        loading.Cancel(); OrderWorkspace.CancelRead(); Model.Section = MainSection.Materials; Model.PageTitle = "Materiały";
        await MaterialCatalog.Activate();
    }
    private async void Operations_Click(object sender, RoutedEventArgs e)
    {
        if (!Model.CanManageCatalog || ActivateOwnedForm()) return;
        loading.Cancel(); OrderWorkspace.CancelRead(); Model.Section = MainSection.Operations; Model.PageTitle = "Operacje i usługi";
        await OperationCatalog.Activate();
    }
}
