using System.Net.Http;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    [Fact]
    public async Task Native_login_forms_and_dirty_window_guard_work_on_windows()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "starting STA thread";
        var thread = new Thread(() =>
        {
            try
            {
                ViewportFailures.Clear();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/FactoryFlow;component/Theme.xaml") }
                };
                stage = "remembered login checkbox and protected Windows storage";
                RunRememberedLoginWorkflow();
                var server = new SyntheticServer();
                using var api = new CrmClient(new Uri("http://localhost/"), server);
                var login = new LoginWindow(api);
                login.Loaded += async (_, _) =>
                {
                    login.SetStartupNotice("Aplikacja jest aktualna.");
                    ((TextBox)login.FindName("Username")).Text = "synthetic.user";
                    await Capture(login, "login");
                    ((PasswordBox)login.FindName("Password")).Password = "synthetic-password";
                    ((ButtonBase)login.FindName("Submit")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                };
                stage = "login window";
                Assert.True(login.ShowDialog());
                Assert.Equal(101, api.Session!.UserId);
                Assert.Equal("crm", api.Session.Role);
                Assert.Equal("synthetic.user", server.LoginUsername);
                var customerForm = new EditorViewModel(EditorKind.Customer);
                var customerWindow = new EditorWindow(api, customerForm);
                customerWindow.Loaded += async (_, _) =>
                {
                    var field = (TextBox)customerWindow.FindName("CustomerName");
                    field.Text = "Synthetic Windows customer";
                    field.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    await Capture(customerWindow, "customer-editor");
                    customerWindow.Width = customerWindow.MinWidth;
                    customerWindow.Height = customerWindow.MinHeight;
                    await Capture(customerWindow, "customer-editor-compact");
                    FindButton(customerWindow, "Zapisz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                };
                stage = "customer form";
                Assert.True(customerWindow.ShowDialog());
                var customer = Assert.IsType<SaveCustomerResult>(customerForm.Saved).Customer;
                Assert.Equal("Synthetic Windows customer", customer.Fields.DisplayName);
                stage = "archived customer rejects first topic without losing native draft";
                server.RejectTopicWrites = true;
                var firstTopicDraft = new EditorViewModel(EditorKind.Topic, customer.Id) { Need = "Synthetic first topic draft" };
                firstTopicDraft.Products[0].Selected = true;
                var firstTopicWindow = new EditorWindow(api, firstTopicDraft);
                Exception? firstTopicFailure = null;
                firstTopicWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        FindButton(firstTopicWindow, "Zapisz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => firstTopicDraft.IsLifecycleRejected && !firstTopicDraft.IsSaving);
                        Assert.True(firstTopicWindow.IsVisible);
                        Assert.False(firstTopicDraft.HasConflict);
                        Assert.Equal("Synthetic first topic draft", firstTopicDraft.Need);
                    }
                    catch (Exception ex) { firstTopicFailure = ex; }
                    finally { firstTopicDraft.Need = ""; firstTopicDraft.Products[0].Selected = false; firstTopicWindow.Close(); }
                };
                firstTopicWindow.ShowDialog();
                if (firstTopicFailure is not null) ExceptionDispatchInfo.Capture(firstTopicFailure).Throw();
                server.RejectTopicWrites = false;
                var topicForm = new EditorViewModel(EditorKind.Topic, customer.Id);
                topicForm.Products[0].Selected = true; topicForm.Need = "Synthetic enquiry";
                var topicWindow = new EditorWindow(api, topicForm);
                topicWindow.Loaded += async (_, _) =>
                {
                    await Capture(topicWindow, "topic-editor");
                    FindButton(topicWindow, "Zapisz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                };
                stage = "topic form";
                Assert.True(topicWindow.ShowDialog());
                var topic = Assert.IsType<TopicDto>(topicForm.Saved);
                stage = "archived customer rejects edited topic without losing native draft";
                server.RejectTopicWrites = true;
                var editedTopicDraft = new EditorViewModel(EditorKind.Topic, customer.Id, topic: topic) { Need = "Synthetic edited topic draft" };
                var editedTopicWindow = new EditorWindow(api, editedTopicDraft);
                Exception? editedTopicFailure = null;
                editedTopicWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        FindButton(editedTopicWindow, "Zapisz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => editedTopicDraft.IsLifecycleRejected && !editedTopicDraft.IsSaving);
                        Assert.True(editedTopicWindow.IsVisible);
                        Assert.False(editedTopicDraft.HasConflict);
                        Assert.Equal("Synthetic edited topic draft", editedTopicDraft.Need);
                    }
                    catch (Exception ex) { editedTopicFailure = ex; }
                    finally { editedTopicDraft.Need = topic.Fields.Need ?? ""; editedTopicWindow.Close(); }
                };
                editedTopicWindow.ShowDialog();
                if (editedTopicFailure is not null) ExceptionDispatchInfo.Capture(editedTopicFailure).Throw();
                server.RejectTopicWrites = false;
                var contactForm = new EditorViewModel(EditorKind.Conversation, customer.Id, topic: topic)
                { DateMode = "text", NextDescription = "po decyzji banku", NextAction = "Zadzwonić", State = "finance" };
                var contactWindow = new EditorWindow(api, contactForm);
                contactWindow.Loaded += async (_, _) =>
                {
                    var note = (TextBox)contactWindow.FindName("Note");
                    note.Text = "Synthetic conversation from native WPF"; note.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    await Capture(contactWindow, "conversation-editor");
                    FindButton(contactWindow, "Zapisz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                };
                stage = "conversation form";
                Assert.True(contactWindow.ShowDialog());
                Assert.Equal("po decyzji banku", Assert.IsType<TopicDto>(contactForm.Saved).Fields.NextContact!.Description);
                var dirty = new EditorViewModel(EditorKind.Customer);
                var dirtyWindow = new EditorWindow(api, dirty);
                Exception? guardFailure = null;
                dirtyWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        dirty.DisplayName = "Synthetic recoverable draft";
                        var dismiss = DismissNo("Niezapisane zmiany");
                        dirtyWindow.Close();
                        await dismiss;
                        Assert.True(dirtyWindow.IsVisible);
                        Assert.Equal("Synthetic recoverable draft", dirty.DisplayName);
                    }
                    catch (Exception ex) { guardFailure = ex; }
                    finally { dirty.DisplayName = ""; dirtyWindow.Close(); }
                };
                stage = "dirty-window confirmation";
                dirtyWindow.ShowDialog();
                if (guardFailure is not null) ExceptionDispatchInfo.Capture(guardFailure).Throw();
                stage = "customer list, detail and contact queue";
                var main = new MainWindow(api);
                Exception? mainFailure = null;
                main.Loaded += async (_, _) =>
                {
                    try
                    {
                        await Until(() => main.Model.Rows.Count == 1);
                        Assert.Equal("Synthetic Windows customer", main.Model.Rows[0].Customer.Fields.DisplayName);
                        var legacy = (Button)main.FindName("LegacyModules");
                        Assert.Equal(Visibility.Collapsed, legacy.Visibility);
                        legacy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        foreach (var role in new[] { "biuro", "technolog", "ceo", "produkcja", "crm", "unknown", null })
                        {
                            server.Role = role;
                            await api.Login("crm", "0000", CancellationToken.None);
                            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            Assert.Equal(Visibility.Collapsed, legacy.Visibility);
                            Assert.Equal(101, api.Session!.UserId);
                        }
                        server.Role = "crm";
                        await api.Login("crm", "0000", CancellationToken.None);
                        Assert.Equal(WindowState.Maximized, main.WindowState);
                        Assert.Equal(WindowStyle.SingleBorderWindow, main.WindowStyle);
                        Assert.Equal(ResizeMode.CanResize, main.ResizeMode);
                        await Capture(main, "customers");
                        main.WindowState = WindowState.Normal;
                        main.Width = main.MinWidth; main.Height = main.MinHeight;
                        await Capture(main, "customers-laptop");
                        var search = (TextBox)main.FindName("Search");
                        Assert.True(search.ActualWidth > 200, $"Search width: {search.ActualWidth}");
                        Assert.True(search.ActualHeight <= 46, $"Single-line search height: {search.ActualHeight}");
                        var customers = (DataGrid)main.FindName("Customers");
                        Assert.True(customers.ActualHeight >= customers.ColumnHeaderHeight + customers.RowHeight,
                            $"Customer grid height {customers.ActualHeight} must fit a {customers.ColumnHeaderHeight}px header and {customers.RowHeight}px row.");
                        var products = (ComboBox)main.FindName("ProductFilter");
                        var toggle = (ToggleButton)products.Template.FindName("Toggle", products);
                        Assert.True(toggle.ActualWidth >= products.ActualWidth - 2,
                            $"ComboBox chrome width {toggle.ActualWidth} must fill its {products.ActualWidth}px control.");
                        main.WindowState = WindowState.Maximized;
                        search.Text = "NoMatchingSynthetic";
                        await Until(() => main.Model.Rows.Count == 0 && !main.Model.Busy);
                        await Capture(main, "customers-empty");
                        Assert.All(((DataGrid)main.FindName("Customers")).Columns, c => Assert.True(c.ActualWidth >= 100));
                        ((TextBox)main.FindName("Search")).Text = "Synthetic";
                        await Until(() => main.Model.Search == "Synthetic" && main.Model.Rows.Count == 1 && main.Model.Status == "Dane aktualne.");
                        ((DataGrid)main.FindName("Customers")).SelectedIndex = 0;
                        FindButton(main, "Otwórz klienta").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Detail is not null && main.Model.History.Count == 1);
                        await Capture(main, "customer-detail");
                        stage = "cancelled topic selection cannot replace a newer selection";
                        var slowTopic = new TopicDto(Guid.NewGuid(), customer.Id, 1, new(["PK1"], "Synthetic slow topic"));
                        var latestTopic = new TopicDto(Guid.NewGuid(), customer.Id, 1, new(["CT1"], "Synthetic latest topic"));
                        main.Model.Topics.Add(new(slowTopic));
                        main.Model.Topics.Add(new(latestTopic));
                        var slowHistory = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var latestHistory = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        server.PendingHistory[slowTopic.Id] = slowHistory;
                        server.PendingHistory[latestTopic.Id] = latestHistory;
                        var selector = (ComboBox)main.FindName("TopicSelector");
                        var originalContext = SynchronizationContext.Current!;
                        var selectionCompletion = new EventCompletionContext(originalContext);
                        try
                        {
                            SynchronizationContext.SetSynchronizationContext(selectionCompletion);
                            selector.SelectedItem = main.Model.Topics.Single(x => x.Topic.Id == slowTopic.Id);
                        }
                        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
                        selector.SelectedItem = main.Model.Topics.Single(x => x.Topic.Id == latestTopic.Id);
                        slowHistory.SetResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50)));
                        await selectionCompletion.Completed.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Equal(latestTopic.Id, Assert.IsType<TopicChoice>(selector.SelectedItem).Topic.Id);
                        latestHistory.SetResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50)));
                        await Until(() => main.Model.SelectedTopic?.Id == latestTopic.Id && !main.Model.HistoryBusy);
                        Assert.Equal(main.Model.SelectedTopic!.Id, Assert.IsType<TopicChoice>(selector.SelectedItem).Topic.Id);
                        selector.SelectedItem = main.Model.Topics.First();
                        await Until(() => main.Model.SelectedTopic?.Id == topic.Id);
                        stage = "cancelled selection stays consistent across module navigation";
                        server.Role = "biuro";
                        await api.Login("biuro", "0000", CancellationToken.None);
                        var navigationHistory = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        server.PendingHistory[slowTopic.Id] = navigationHistory;
                        selectionCompletion = new EventCompletionContext(originalContext);
                        try
                        {
                            SynchronizationContext.SetSynchronizationContext(selectionCompletion);
                            selector.SelectedItem = main.Model.Topics.Single(x => x.Topic.Id == slowTopic.Id);
                        }
                        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
                        FindButton(main, "Tetrapod").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(MainSection.Tetrapod, main.Model.Section);
                        navigationHistory.SetResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50)));
                        await selectionCompletion.Completed.WaitAsync(TimeSpan.FromSeconds(5));
                        FindButton(main, "Klienci").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(MainSection.Detail, main.Model.Section);
                        Assert.Equal(topic.Id, main.Model.SelectedTopic!.Id);
                        Assert.Equal(topic.Id, Assert.IsType<TopicChoice>(selector.SelectedItem).Topic.Id);
                        Assert.True(main.Model.CanActOnTopic);
                        stage = "history retry cannot leave a different topic displayed";
                        var retryHistory = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        server.PendingHistory[slowTopic.Id] = retryHistory;
                        selectionCompletion = new EventCompletionContext(originalContext);
                        try
                        {
                            SynchronizationContext.SetSynchronizationContext(selectionCompletion);
                            selector.SelectedItem = main.Model.Topics.Single(x => x.Topic.Id == slowTopic.Id);
                        }
                        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
                        main.Model.HistoryPage = 2;
                        FindButton(main, "← Historia").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        retryHistory.SetResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50)));
                        await selectionCompletion.Completed.WaitAsync(TimeSpan.FromSeconds(5));
                        await Until(() => !main.Model.HistoryBusy);
                        Assert.Equal(topic.Id, main.Model.SelectedTopic!.Id);
                        Assert.Equal(topic.Id, Assert.IsType<TopicChoice>(selector.SelectedItem).Topic.Id);
                        server.Role = "crm";
                        await api.Login("crm", "0000", CancellationToken.None);
                        main.WindowState = WindowState.Normal;
                        main.Width = main.MinWidth; main.Height = main.MinHeight;
                        await Capture(main, "customer-detail-laptop");
                        main.WindowState = WindowState.Maximized;
                        FindButton(main, "Do kontaktu").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        main.Model.QueueGroup = "unclear";
                        await Until(() => main.Model.Queue && main.Model.Rows.Count == 1 && main.Model.Status == "Dane aktualne.");
                        Assert.Contains("po decyzji banku", main.Model.Rows[0].Next);
                        Assert.Equal(main.FindResource("Lime"), ((Button)main.FindName("QueueNav")).Background);
                        Assert.Equal(main.FindResource("Sidebar"), ((Button)main.FindName("QueueNav")).Foreground);
                        await Capture(main, "contact-queue");
                        stage = "archive retry preserves history and restore returns contact plan";
                        FindButton(main, "Klienci").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Section == MainSection.Customers && !main.Model.Busy && main.Model.Rows.Count == 1);
                        customers.SelectedIndex = 0;
                        FindButton(main, "Otwórz klienta").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.IsDetailVisible && !main.Model.HistoryBusy);
                        server.LoseArchiveReply = true;
                        var archiveDialog = DriveLifecycle(main, async dialog =>
                        {
                            ((ButtonBase)dialog.FindName("Confirm")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Until(() => dialog.Model.IsUncertain && !dialog.Model.IsSaving);
                            Assert.True(dialog.IsVisible);
                            var dismiss = DismissNo("Niepotwierdzona operacja");
                            dialog.Close(); await dismiss;
                            Assert.True(dialog.IsVisible);
                            ((ButtonBase)dialog.FindName("Confirm")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Until(() => !dialog.IsVisible);
                        });
                        FindButton(main, "Usuń klienta").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await archiveDialog;
                        await Until(() => main.Model.Section == MainSection.Customers && !main.Model.Busy && main.Model.Rows.Count == 0);
                        Assert.Single(server.LifecycleReceipts);
                        FindButton(main, "Do kontaktu").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Section == MainSection.Queue && !main.Model.Busy);
                        Assert.Empty(main.Model.Rows);
                        FindButton(main, "Usunięci klienci").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Section == MainSection.Archived && !main.Model.Busy && main.Model.Rows.Count == 1);
                        customers.SelectedIndex = 0;
                        FindButton(main, "Otwórz klienta").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.IsDetailVisible && main.Model.IsArchivedCustomer && !main.Model.HistoryBusy);
                        Assert.False(FindButton(main, "Edytuj dane").IsEnabled);
                        Assert.False(FindButton(main, "+ Dodaj temat").IsEnabled);
                        Assert.False(FindButton(main, "Zapisz rozmowę").IsEnabled);
                        Assert.Contains(main.Model.History, x => x.Event.Note == "Synthetic conversation from native WPF");
                        await Capture(main, "archived-customer");
                        var restoreDialog = DriveLifecycle(main, async dialog =>
                        {
                            ((ButtonBase)dialog.FindName("Confirm")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Until(() => !dialog.IsVisible);
                        });
                        FindButton(main, "Przywróć klienta").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await restoreDialog;
                        await Until(() => main.Model.IsDetailVisible && main.Model.CanEditCustomer && !main.Model.HistoryBusy);
                        Assert.Equal(customer.Id, main.Model.Detail!.Customer.Id);
                        Assert.Equal("po decyzji banku", main.Model.SelectedTopic!.Fields.NextContact!.Description);
                        Assert.Equal(2, server.LifecycleReceipts.Count);
                        FindButton(main, "Do kontaktu").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Section == MainSection.Queue && !main.Model.Busy && main.Model.Rows.Count == 1);
                        stage = "CRM and Tetrapod share one window and retain calculation input";
                        server.Role = "biuro"; server.Assigned = true;
                        await api.Login("biuro", "0000", CancellationToken.None);
                        FindButton(main, "Tetrapod").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        var sharedCalculator = (TetrapodPage)main.FindName("Tetrapod");
                        ((TextBox)sharedCalculator.FindName("Planned")).Text = "7";
                        FindButton(main, "Klienci").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => main.Model.Section == MainSection.Customers);
                        FindButton(main, "Tetrapod").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal("7", sharedCalculator.Model.Planned);
                        Assert.Equal(MainSection.Tetrapod, main.Model.Section);
                        main.WindowState = WindowState.Normal; main.Width = 1366; main.Height = 768;
                        await Capture(main, "tetrapod-ci-window");
                        main.WindowState = WindowState.Maximized;
                        stage = "ordinary role shell and Tetrapod calculator";
                        server.Role = "biuro"; server.Assigned = false;
                        var crmReads = server.CrmReads;
                        await api.Login("biuro", "0000", CancellationToken.None);
                        await main.Model.Load(CancellationToken.None);
                        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Assert.False(main.Model.HasCrmAccess);
                        Assert.Equal(crmReads, server.CrmReads);
                        Assert.Equal(Visibility.Collapsed, ((Button)main.FindName("ClientsNav")).Visibility);
                        var calculatorButton = (Button)main.FindName("TetrapodNav");
                        Assert.Equal(Visibility.Visible, calculatorButton.Visibility);
                        calculatorButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        var calculator = (TetrapodPage)main.FindName("Tetrapod");
                        await Until(() => calculator.IsVisible);
                        Assert.Single(Application.Current.Windows.OfType<MainWindow>());
                        ((TextBox)calculator.FindName("Planned")).Text = "10";
                        ((TextBox)calculator.FindName("Completed")).Text = "2";
                        ((TextBox)calculator.FindName("Delivery6")).Text = "100,25";
                        server.ExpireCalculation = true;
                        var reauthenticate = main.Dispatcher.InvokeAsync(async () =>
                        {
                            await Until(() => Application.Current.Windows.OfType<LoginWindow>().Any(w => w.Owner == main && w.IsLoaded));
                            var renewal = Application.Current.Windows.OfType<LoginWindow>().Single(w => w.Owner == main);
                            ((TextBox)renewal.FindName("Username")).Text = "synthetic.user";
                            ((PasswordBox)renewal.FindName("Password")).Password = "synthetic-password";
                            ((ButtonBase)renewal.FindName("Submit")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Until(() => !renewal.IsVisible);
                        }).Task.Unwrap();
                        FindButton(calculator, "Oblicz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await reauthenticate;
                        Assert.Equal("10", calculator.Model.Planned);
                        Assert.Equal("2", calculator.Model.Completed);
                        Assert.Equal("100,25", calculator.Model.Delivery6);
                        FindButton(calculator, "Oblicz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => calculator.Model.HasResult);
                        Assert.Equal(88.125, calculator.Model.Result!.WeightsKg.Planned);
                        Assert.Equal(100.25, server.Calculation!.DeliveriesKg![6]);
                        await Capture(main, "tetrapod-result");
                        main.WindowState = WindowState.Normal;
                        main.Width = main.MinWidth; main.Height = main.MinHeight;
                        await Capture(main, "tetrapod-laptop");
                        main.WindowState = WindowState.Maximized;
                        Assert.Equal("100,25", calculator.Model.Delivery6);
                        var dismiss = DismissNo("Dane kalkulatora");
                        main.Close();
                        await dismiss;
                        Assert.True(main.IsVisible);
                        Assert.Equal("100,25", calculator.Model.Delivery6);
                        calculator.Model.Planned = ""; calculator.Model.Completed = "0";
                        calculator.Model.Delivery6 = calculator.Model.Delivery12 = calculator.Model.Delivery16 = "";
                        var ownedDraft = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic owned draft" };
                        var ownedEditor = new EditorWindow(api, ownedDraft) { Owner = main };
                        Exception? ownerFailure = null;
                        ownedEditor.Loaded += (_, _) =>
                        {
                            try
                            {
                                main.Close();
                                Assert.True(main.IsVisible);
                                Assert.True(ownedEditor.IsVisible);
                                Assert.Equal("Synthetic owned draft", ownedDraft.DisplayName);
                            }
                            catch (Exception ex) { ownerFailure = ex; }
                            finally { ownedDraft.DisplayName = ""; ownedEditor.Close(); }
                        };
                        ownedEditor.ShowDialog();
                        if (ownerFailure is not null) ExceptionDispatchInfo.Capture(ownerFailure).Throw();
                        var ownedTemplate = new OrderTemplateWindow(api, new() { Id = 1 }) { Owner = main };
                        var initialTemplateName = ownedTemplate.Model.Name;
                        ownedTemplate.Model.Name = "Synthetic owned template draft";
                        ownedTemplate.Loaded += (_, _) =>
                        {
                            try
                            {
                                var session = api.Session;
                                main.Close();
                                FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                Assert.True(main.IsVisible); Assert.True(ownedTemplate.IsVisible);
                                Assert.Same(session, api.Session);
                                Assert.True(ownedTemplate.Model.Dirty);
                                Assert.Equal("Synthetic owned template draft", ownedTemplate.Model.Name);
                            }
                            catch (Exception ex) { ownerFailure = ex; }
                            finally { ownedTemplate.Model.Name = initialTemplateName; ownedTemplate.Close(); }
                        };
                        ownedTemplate.ShowDialog();
                        if (ownerFailure is not null) ExceptionDispatchInfo.Capture(ownerFailure).Throw();
                        var ownedDocuments = new OrderDocumentsWindow(api, new() { Id = 1 }) { Owner = main };
                        ownedDocuments.Model.Busy = true;
                        ownedDocuments.Loaded += (_, _) =>
                        {
                            try
                            {
                                var session = api.Session;
                                main.Close();
                                FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                Assert.True(main.IsVisible); Assert.True(ownedDocuments.IsVisible);
                                Assert.Same(session, api.Session); Assert.True(ownedDocuments.Model.Busy);
                            }
                            catch (Exception ex) { ownerFailure = ex; }
                            finally { ownedDocuments.Model.Busy = false; ownedDocuments.Close(); }
                        };
                        ownedDocuments.ShowDialog();
                        if (ownerFailure is not null) ExceptionDispatchInfo.Capture(ownerFailure).Throw();
                    }
                    catch (Exception ex) { mainFailure = ex; }
                    finally { main.Close(); }
                };
                main.ShowDialog();
                if (mainFailure is not null) ExceptionDispatchInfo.Capture(mainFailure).Throw();
                stage = "native orders creation, triage and quote";
                RunOrdersWorkflow();
                stage = "native office question queue and repeatable answers";
                RunOrderQuestionQueueWorkflow();
                stage = "native materials and operations catalog";
                RunCatalogWorkflow();
                stage = "native SOP templates and drawing review";
                RunTemplatesWorkflow();
                stage = "background updates and idle restart guards";
                RunBackgroundUpdatesWorkflow();
                stage = "native shift reports and manual update guards";
                RunShiftReportsWorkflow();
                stage = "native reports, office history and XLSX";
                RunInsightsWorkflow();
                RunLegoWorkflow();
                stage = "native Tetrapod local delivery history";
                RunTetrapodDeliveryWorkflow();
                stage = "shared Tetrapod contracts and steel deliveries";
                RunProductionWorkflow();
                stage = "production report allocation and assigned acceptance";
                RunProductionReviewWorkflow();
                try { Assert.True(ViewportFailures.Count == 0, "Native viewport failures:" + Environment.NewLine + string.Join(Environment.NewLine, ViewportFailures)); }
                finally { app.Shutdown(); }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(180)); }
        catch (TimeoutException) when (!completion.Task.IsCompleted) { throw new TimeoutException($"Native workflow stalled at: {stage}"); }
    }
    private static async Task DriveLifecycle(Window owner, Func<CustomerLifecycleWindow, Task> action)
    {
        await Until(() => owner.OwnedWindows.OfType<CustomerLifecycleWindow>().Any());
        await action(owner.OwnedWindows.OfType<CustomerLifecycleWindow>().Single());
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var n = 0; n < 100; n++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The window did not reach the expected state.");
    }
    private sealed class EventCompletionContext(SynchronizationContext inner) : SynchronizationContext
    {
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int operations;
        public Task Completed => completed.Task;
        public override void Post(SendOrPostCallback callback, object? state) => inner.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => inner.Send(callback, state);
        public override void OperationStarted() => Interlocked.Increment(ref operations);
        public override void OperationCompleted() { if (Interlocked.Decrement(ref operations) == 0) completed.TrySetResult(); }
    }
    private static async Task Capture(Window window, string name)
    {
        await VerifyViewport(window, name);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        SaveSnapshot(window, name);
    }
    private static void SaveSnapshot(Window window, string name)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        var paper = new DrawingVisual();
        using (var drawing = paper.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(paper);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Environment.GetEnvironmentVariable("FACTORYFLOW_UI_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "rcm-ui-tests");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static ButtonBase FindButton(DependencyObject parent, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is ButtonBase button && button.Content?.ToString() == content) return button;
            try { return FindButton(child, content); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Button {content} not found.");
    }
    private static Task DismissNo(string title) => Task.Run(async () =>
    {
        var seen = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var dialog = FindWindow("#32770", title);
            if (dialog == IntPtr.Zero) dialog = FindWindow(null, title);
            if (dialog == IntPtr.Zero)
            {
                if (seen) return;
            }
            else
            {
                seen = true;
                var button = GetDlgItem(dialog, 7);
                if (button != IntPtr.Zero) SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                else SendMessage(dialog, 0x0111, new IntPtr(7), IntPtr.Zero);
            }
            await Task.Delay(50);
        }
        var remaining = FindWindow(null, title);
        if (remaining != IntPtr.Zero) SendMessage(remaining, 0x0111, new IntPtr(6), IntPtr.Zero);
        throw new TimeoutException($"Confirmation '{title}' could not be dismissed with No.");
    });
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private sealed class SyntheticServer : HttpMessageHandler
    {
        public string? Role { get; set; } = "crm";
        public string? LoginRole { get; private set; }
        public string? LoginUsername { get; private set; }
        public bool Assigned { get; set; } = true;
        public int CrmReads { get; private set; }
        public TetrapodInput? Calculation { get; private set; }
        public bool ExpireCalculation { get; set; }
        public bool RejectTopicWrites { get; set; }
        public bool LoseArchiveReply { get; set; }
        public Dictionary<Guid, CustomerDto> LifecycleReceipts { get; } = [];
        public Dictionary<Guid, TaskCompletionSource<HttpResponseMessage>> PendingHistory { get; } = [];
        private readonly Guid customerId = Guid.NewGuid(), topicId = Guid.NewGuid(), teamId = Guid.NewGuid();
        private CustomerDto? customer;
        private TopicDto? topic;
        private ContactEventDto? contactEvent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/shift-reports/features") return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures);
            if (path.EndsWith("/login/password"))
            {
                var credentials = (await request.Content!.ReadFromJsonAsync<PasswordLoginRequest>(ct))!;
                LoginUsername = credentials.Username;
                return EditorTests.Reply(new { access_token = "synthetic-session", id = 101,
                    refresh_token = credentials.RememberMe ? "synthetic-device-token" : null });
            }
            if (path.EndsWith("/login"))
            {
                LoginRole = (await request.Content!.ReadFromJsonAsync<LoginRequest>(ct))!.Role;
                return EditorTests.Reply(new { access_token = "synthetic-session" });
            }
            if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(101, "Synthetic UI user", Assigned ? teamId : null, Assigned ? "Synthetic team" : null, Role));
            if (path == "/api/v1/orders/features") return EditorTests.Reply(new OrderFeatures(false));
            if (path.EndsWith("/calculators/tetrapod"))
            {
                if (ExpireCalculation)
                {
                    ExpireCalculation = false;
                    return EditorTests.Reply(new ApiError(401, "Synthetic expired session"), System.Net.HttpStatusCode.Unauthorized);
                }
                Calculation = await request.Content!.ReadFromJsonAsync<TetrapodInput>(ct);
                return EditorTests.Reply(TetrapodViewModelTests.Result());
            }
            if (request.Method == HttpMethod.Get)
            {
                CrmReads++;
                if (path.EndsWith("/customers/summaries"))
                {
                    if (customer!.ArchivedAt is not null || request.RequestUri.Query.Contains("NoMatchingSynthetic"))
                        return EditorTests.Reply(new Page<CustomerSummaryRow>([], 0, 1, 50));
                    return EditorTests.Reply(new Page<CustomerSummaryRow>([new(customer, [new(topic!.Id, topic.Fields.Products, topic.Fields.State, topic.Fields.NextContact)], 1, contactEvent?.RecordedAt)], 1, 1, 50));
                }
                if (path.EndsWith("/customers/archived"))
                    return EditorTests.Reply(new Page<CustomerDto>(customer!.ArchivedAt is not null ? [customer] : [], customer.ArchivedAt is not null ? 1 : 0, 1, 50));
                if (path.EndsWith("/record")) return EditorTests.Reply(customer!);
                if (path.EndsWith("/topics")) return EditorTests.Reply(new Page<TopicDto>(topic is null ? [] : [topic], topic is null ? 0 : 1, 1, 50));
                if (path.EndsWith($"/topics/{topicId}")) return EditorTests.Reply(topic!);
                if (path.EndsWith("/history"))
                {
                    var id = Guid.Parse(path.Split('/')[^2]);
                    if (PendingHistory.TryGetValue(id, out var pending)) return await pending.Task;
                    return EditorTests.Reply(new Page<ContactEventDto>([contactEvent!], 1, 1, 50));
                }
                if (path.EndsWith("/queue")) return EditorTests.Reply(new Page<QueueRow>(customer!.ArchivedAt is null ? [new(customer, topic!, "unclear")] : [], customer.ArchivedAt is null ? 1 : 0, 1, 50));
                return EditorTests.Reply(new CustomerDetail(customer!, [topic!]));
            }
            if (path.EndsWith("/archive") || path.EndsWith("/restore"))
            {
                var command = (await request.Content!.ReadFromJsonAsync<ChangeCustomerLifecycle>(ct))!;
                if (LifecycleReceipts.TryGetValue(command.RequestId, out var prior)) return EditorTests.Reply(prior);
                if (command.ExpectedVersion != customer!.Version) return EditorTests.Reply(new ApiError(409, "Synthetic conflict"), System.Net.HttpStatusCode.Conflict);
                var restore = path.EndsWith("/restore");
                customer = customer with { Version = customer.Version + 1, ArchivedAt = restore ? null : DateTimeOffset.UtcNow, ArchivedBy = restore ? null : 101 };
                LifecycleReceipts[command.RequestId] = customer;
                if (LoseArchiveReply && !restore) { LoseArchiveReply = false; throw new HttpRequestException("Synthetic lost reply after commit"); }
                return EditorTests.Reply(customer);
            }
            if (RejectTopicWrites && (path.EndsWith("/topics") || path.Contains("/topics/", StringComparison.Ordinal)))
                return EditorTests.Reply(new ApiError(410, "Klient został usunięty. Przywróć go przed zapisaniem zmian."), System.Net.HttpStatusCode.Gone);
            if (path.EndsWith("/customers"))
            {
                var cmd = (await request.Content!.ReadFromJsonAsync<CreateCustomer>(ct))!;
                customer = new(customerId, 1, cmd.Fields, true, DateTimeOffset.UtcNow);
                return EditorTests.Reply(new SaveCustomerResult(customer, []));
            }
            if (path.EndsWith("/topics"))
            {
                var cmd = (await request.Content!.ReadFromJsonAsync<CreateTopic>(ct))!;
                topic = new(topicId, customerId, 1, cmd.Fields);
                return EditorTests.Reply(topic);
            }
            var contact = (await request.Content!.ReadFromJsonAsync<RecordContact>(ct))!;
            topic = new(topicId, customerId, 2, new(["CT1"], "Synthetic enquiry", contact.State, contact.NextContact));
            contactEvent = new(Guid.NewGuid(), topicId, 101, "Synthetic UI user", DateTimeOffset.UtcNow, "conversation", contact.Note, contact.State, contact.NextContact, null);
            return EditorTests.Reply(topic);
        }
    }
}
