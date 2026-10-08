using System.Runtime.ExceptionServices;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunOrdersWorkflow()
    {
        var server = new NativeOrdersServer { LoseCreateResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        var page = new OrdersPage(); page.SetClient(api);
        var window = new Window { Title = "Synthetic orders workflow", Content = page, Width = 1180, Height = 760 };
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await api.Login("biuro", "0000", CancellationToken.None);
                await page.Activate(); Assert.Single(page.Model.Rows);
                await Capture(window, "orders-list");
                var delayedList = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                server.NextList = delayedList;
                var staleRead = page.Activate();
                await page.Activate();
                ((DataGrid)page.FindName("Orders")).SelectedIndex = 0;
                FindButton(page, "Otwórz zlecenie").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => page.Model.DetailVisible && !page.Model.Busy);
                var delayedTriage = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                server.PendingTriage = delayedTriage;
                FindButton(page, "Sprawdź i skieruj zlecenie").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(page.IsSaving);
                delayedList.SetResult(EditorTests.Reply(new Page<OrderDto>([server.Order], 1, 1, 50)));
                await staleRead;
                Assert.True(page.IsSaving);
                delayedTriage.SetResult(EditorTests.Reply(new OrderTriageResult("niestandard", "Do wyceny", null, null, [])));
                await Until(() => !page.IsSaving);
                var editorFlow = DriveOrderEditor(window, async editor =>
                {
                    Assert.True(editor.Model.Intake);
                    Assert.Equal("Przyjmij i skieruj", ((ButtonBase)editor.FindName("Save")).Content);
                    ((Expander)editor.FindName("InternalFirms")).IsExpanded = true; editor.UpdateLayout();
                    FindButton(editor, "Demo Concrete").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(editor.Model.InternalOrder); Assert.Equal("Demo Concrete", editor.Model.Client);
                    FindButton(editor, "Demo Concrete").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.False(editor.Model.InternalOrder);
                    FindButton(editor, "Zbrojenie / MON").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(editor.Model.Defence); Assert.Equal("zbrojenie", editor.Model.OrderType);
                    FindButton(editor, "Remont").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.False(editor.Model.Defence);
                    editor.Model.Client = "Synthetic native order";
                    editor.Model.Description = "Repair of synthetic frame";
                    editor.Model.Quantity = "2";
                    editor.Model.Contact = "Synthetic contact";
                    await Capture(editor, "order-editor");
                    ((ButtonBase)editor.FindName("Save")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Saving);
                    Assert.Equal("Repair of synthetic frame", editor.Model.Description);
                    var dismiss = DismissNo("Niezapisane dane"); editor.Close(); await dismiss;
                    Assert.True(editor.IsVisible);
                    ((ButtonBase)editor.FindName("Save")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(page, "+ Nowe zlecenie").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await editorFlow;
                await Until(() => page.Model.DetailVisible && !page.Model.Busy);
                Assert.Single(server.Receipts); Assert.Equal("Synthetic native order", page.Model.Detail!.Client);
                Assert.Equal("niestandard", page.Model.Detail.Status); Assert.Single(server.IntakeReceipts);
                Assert.Contains("Synthetic warning", page.Model.Status);
                server.Role = "technolog"; await api.Login("technolog", "0000", CancellationToken.None); page.Model.NotifyActions();
                await Capture(window, "order-detail");
                var quoteFlow = DriveOrderQuote(window, async editor =>
                {
                    await Until(() => editor.Model.Loaded && !editor.Model.Busy);
                    editor.Model.Processes.Add(new() { Name = "Synthetic welding", Hours = "2", Rate = "100" });
                    editor.Model.Materials.Add(new() { Name = "Synthetic steel", Quantity = "10", Price = "5" });
                    FindButton(editor, "Oblicz").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Result.Contains("PLN netto") && !editor.Model.Busy);
                    await Capture(editor, "order-quote");
                    var dismiss = DismissNo("Niezapisana wycena"); editor.Close(); await dismiss;
                    Assert.True(editor.IsVisible); Assert.Single(editor.Model.Processes);
                    ((ButtonBase)editor.FindName("Save")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(page, "Wycena").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await quoteFlow;
                await Until(() => page.Model.Detail?.Status == "quoted" && !page.Model.Busy);
                Assert.Equal(1, server.QuoteSaves);
                await page.Activate(); Assert.Equal("Synthetic native order", page.Model.Detail!.Client);
                var resourceFlow = DriveOrderResources(window, async editor =>
                {
                    await Until(() => editor.Model.Operations.Count == 1 && !editor.Model.Busy);
                    ((DataGrid)editor.FindName("ResourcesOperations")).SelectedIndex = 0;
                    editor.Model.Draft = "2,5";
                    ((ButtonBase)editor.FindName("SaveResource")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => server.Operation.ActualHours == 2.5 && !editor.Model.Busy);
                    ((ButtonBase)editor.FindName("NewQuestion")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    editor.Model.Draft = "Jaka grubość materiału dla ramy próbnej?";
                    await Capture(editor, "order-resources");
                    var dismiss = DismissNo("Niezapisane dane"); editor.Close(); await dismiss;
                    Assert.True(editor.IsVisible);
                    server.LoseQuestionResponse = true;
                    ((ButtonBase)editor.FindName("SaveResource")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                    Assert.Equal("Jaka grubość materiału dla ramy próbnej?", editor.Model.Draft);
                    Assert.False(editor.Model.CanEdit);
                    ((ButtonBase)editor.FindName("SaveResource")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Dirty && !editor.Model.Busy);
                    Assert.Single(server.Questions); Assert.Equal(2, server.ResourceReceipts.Count);
                    Assert.Equal(server.ResourceRequestIds[^1], server.ResourceRequestIds[^2]);
                    editor.Close();
                });
                FindButton(page, "Operacje i pytania").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await resourceFlow;
                await Until(() => !page.Model.Busy);
                var attachmentsFlow = DriveOrderAttachments(window, async editor =>
                {
                    await Until(() => editor.Model.Rows.Count == 1 && !editor.Model.Busy);
                    ((DataGrid)editor.FindName("AttachmentList")).SelectedIndex = 0;
                    Assert.True(editor.Model.CanDownload);
                    await Capture(editor, "order-attachments");
                    var file = Path.Combine(Path.GetTempPath(), "rcm-synthetic-" + Guid.NewGuid().ToString("N") + ".pdf");
                    try
                    {
                        await editor.Model.Download(api, file, CancellationToken.None);
                        Assert.Equal("%PDF-1.7\nSynthetic", await File.ReadAllTextAsync(file));
                    }
                    finally { File.Delete(file); }
                    editor.Close();
                });
                FindButton(page, "Załączniki").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await attachmentsFlow;
                var documentsFlow = DriveOrderDocuments(window, async editor =>
                {
                    Assert.True(editor.Model.CanDownload); Assert.True(editor.Model.CanOperations);
                    await Capture(editor, "order-documents");
                    var folder = Path.Combine(Path.GetTempPath(), "rcm-synthetic-documents-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    try
                    {
                        foreach (var kind in new[] { "arkusz", "oferta", "operations" })
                        {
                            var file = Path.Combine(folder, editor.Model.Filename(kind));
                            await editor.Model.Download(api, kind, file, default);
                            Assert.True(File.Exists(file));
                            Assert.StartsWith(kind == "operations" ? "PK" : "%PDF", await File.ReadAllTextAsync(file));
                        }
                    }
                    finally { Directory.Delete(folder, true); }
                    editor.Close();
                });
                FindButton(page, "Dokumenty").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await documentsFlow;
                var templateFlow = DriveOrderTemplate(window, async editor =>
                {
                    editor.Model.Name = "Synthetic reusable frame";
                    editor.Model.Category = "remont";
                    await Capture(editor, "order-template");
                    var dismiss = DismissNo("Niezapisany szablon"); editor.Close(); await dismiss;
                    Assert.True(editor.IsVisible); Assert.Equal("Synthetic reusable frame", editor.Model.Name);
                    server.LoseTemplateResponse = true;
                    ((ButtonBase)editor.FindName("SaveTemplate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                    Assert.False(((TextBox)editor.FindName("TemplateName")).IsEnabled);
                    Assert.True(editor.Model.Dirty);
                    ((ButtonBase)editor.FindName("SaveTemplate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                    Assert.Single(server.TemplateReceipts); Assert.Equal(server.TemplateRequestIds[0], server.TemplateRequestIds[1]);
                });
                FindButton(page, "Zapisz jako szablon").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await templateFlow;
                Assert.Contains("Synthetic reusable frame", page.Model.Status);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        };
        window.ShowDialog();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task DriveOrderEditor(Window owner, Func<OrderEditorWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderEditorWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderEditorWindow>().Single());
    }
    private static async Task DriveOrderQuote(Window owner, Func<OrderQuoteWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderQuoteWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderQuoteWindow>().Single());
    }
    private static async Task DriveOrderResources(Window owner, Func<OrderResourcesWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderResourcesWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderResourcesWindow>().Single());
    }
    private static async Task DriveOrderAttachments(Window owner, Func<OrderAttachmentsWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderAttachmentsWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderAttachmentsWindow>().Single());
    }
    private static async Task DriveOrderDocuments(Window owner, Func<OrderDocumentsWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderDocumentsWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderDocumentsWindow>().Single());
    }
    private static async Task DriveOrderTemplate(Window owner, Func<OrderTemplateWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<OrderTemplateWindow>().Any());
        await run(owner.OwnedWindows.OfType<OrderTemplateWindow>().Single());
    }
}
