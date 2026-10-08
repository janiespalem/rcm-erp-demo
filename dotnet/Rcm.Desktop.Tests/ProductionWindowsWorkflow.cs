using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunProductionWorkflow()
    {
        var server = new ProductionServer(); using var api = ProductionTests.Login(server).GetAwaiter().GetResult();
        var page = new ProductionPage(); page.SetClient(api);
        var owner = new Window { Title = "Synthetic production workflow", Width = 1360, Height = 950, Style = (Style)Application.Current.FindResource("WindowStyle"), Content = page };
        Exception? failure = null;
        owner.Loaded += async (_, _) =>
        {
            try
            {
                await page.Activate(); Assert.True(page.Model.CanCreate);
                ((DataGrid)page.FindName("ProductionContracts")).SelectedIndex = 0;
                await Until(() => page.Model.Detail is not null && !page.Model.DetailBusy);
                Assert.True(((ButtonBase)page.FindName("AddSteelDelivery")).IsEnabled); Assert.True(page.Model.Deliveries[0].Delivery.BeforeOpening);
                await Capture(owner, "production-contract-material-potential");
                var flow = DriveProductionEditor(owner, async editor =>
                {
                    Assert.IsType<SteelDeliveryWindow>(editor); editor.Model.Field("d6").Value = "3,768"; editor.Model.Field("d12").Value = "0"; editor.Model.Field("d16").Value = "49,056";
                    Assert.Equal(server.Features.Today.ToDateTime(TimeOnly.MinValue), ((DatePicker)editor.Form.FindName("SteelDeliveryDate")).SelectedDate);
                    var dismiss = DismissNo("Niezapisane dane produkcji"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    await Capture(editor, "production-steel-delivery-form");
                    editor.Model.Field("d6").Value = "0.0001"; ((ButtonBase)editor.Form.FindName("SaveProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Errors.Count > 0 && !editor.Model.Busy); Assert.Equal("0.0001", editor.Model.Field("d6").Value); Assert.Empty(server.Bodies); editor.Model.Field("d6").Value = "3,768";
                    server.LoseNext = true; ((ButtonBase)editor.Form.FindName("SaveProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy); Assert.False(editor.Model.CanEdit); editor.Close(); Assert.True(editor.IsVisible);
                    Assert.Equal("3,768", editor.Model.Field("d6").Value); await Capture(editor, "production-steel-delivery-save-unknown");
                    ((ButtonBase)editor.Form.FindName("RetryProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Committed && !editor.Model.Busy && !editor.Model.Uncertain); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.False(editor.Model.Dirty); editor.Close();
                });
                ((ButtonBase)page.FindName("AddSteelDelivery")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                await Until(() => !page.Model.Busy && !page.Model.DetailBusy && page.Model.Detail?.Version == 2);
                ((DataGrid)page.FindName("ProductionDeliveries")).SelectedIndex = 0;
                flow = DriveProductionEditor(owner, async editor =>
                {
                    Assert.False(editor.Model.IsNew); editor.Model.Field("d6").Value = "4,001"; ((TextBox)editor.Form.FindName("ProductionReason")).Text = "Synthetic corrected measured mass";
                    server.Contract = server.Contract with { Version = 3 }; server.Delivery = server.Delivery with { Version = 2 };
                    ((ButtonBase)editor.Form.FindName("SaveProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.HasConflict && !editor.Model.Busy); Assert.Equal("4,001", editor.Model.Field("d6").Value);
                    ((ButtonBase)editor.Form.FindName("CompareProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => editor.Model.CanAcceptComparison);
                    await Capture(editor, "production-steel-correction-conflict");
                    ((ButtonBase)editor.Form.FindName("AcceptProductionVersion")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Equal("4,001", editor.Model.Field("d6").Value);
                    ((ButtonBase)editor.Form.FindName("SaveProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => editor.Model.Committed && !editor.Model.Busy); Assert.Equal(4001, server.Delivery.Fields.Steel.Diameter6); editor.Close();
                });
                ((ButtonBase)page.FindName("CorrectSteelDelivery")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                await Until(() => !page.Model.Busy && !page.Model.DetailBusy);
                ((TabControl)page.FindName("ProductionTabs")).SelectedIndex = 1;
                FindButton(owner, "Wczytaj historię").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.Audit.Count == 1 && !page.Model.DetailBusy);
                Assert.Equal("Korekta dostawy", page.Model.Audit[0].Action); Assert.Contains("→ Ø6: 4 kg", page.Model.Audit[0].Changes); Assert.DoesNotContain("{", page.Model.Audit[0].Changes);
                await Capture(owner, "production-readable-audit"); owner.Width = 1280; owner.Height = 800; await Capture(owner, "production-readable-audit-laptop");
                ((TabControl)page.FindName("ProductionTabs")).SelectedIndex = 0;
                var refreshedVersion = server.Contract.Version + 1;
                server.Contract = server.Contract with { Version = refreshedVersion, Fields = server.Contract.Fields with { Name = "Synthetic refreshed contract", PlannedTetrapods = 125 } };
                server.Delivery = server.Delivery with { Version = server.Delivery.Version + 1, Fields = server.Delivery.Fields with { Note = "Synthetic refreshed delivery" } };
                Assert.NotEqual(server.Contract.Fields.Name, page.Model.Detail!.Fields.Name);
                ((ButtonBase)page.FindName("RefreshProduction")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => !page.Model.Busy && !page.Model.DetailBusy && page.Model.Detail?.Version == refreshedVersion && page.Model.Deliveries.FirstOrDefault()?.Note == "Synthetic refreshed delivery");
                Assert.Equal(server.Contract.Id, page.Model.Selected!.Contract.Id); Assert.Equal("Synthetic refreshed contract", page.Model.Detail!.Fields.Name); Assert.Equal(125, page.Model.Detail.Fields.PlannedTetrapods);
                Assert.Equal(server.Delivery.Version, Assert.Single(page.Model.Deliveries).Delivery.Version);
                server.Role = "produkcja"; await api.Login("produkcja", "0000", default); await page.Activate();
                ((DataGrid)page.FindName("ProductionContracts")).SelectedIndex = 0; await Until(() => page.Model.Detail is not null && !page.Model.DetailBusy);
                Assert.False(((ButtonBase)page.FindName("NewProductionContract")).IsEnabled); Assert.False(((ButtonBase)page.FindName("AddSteelDelivery")).IsEnabled); Assert.True(((ButtonBase)page.FindName("EditProductionContract")).IsEnabled);
                await Capture(owner, "production-contract-production-readonly");
                flow = DriveProductionEditor(owner, async editor =>
                {
                    Assert.IsType<ProductionContractWindow>(editor); Assert.False(editor.Model.CanEdit); Assert.False(((ButtonBase)editor.Form.FindName("SaveProduction")).IsEnabled); Assert.False(((CheckBox)editor.Form.FindName("ProductionNormConfirmed")).IsEnabled); await Capture(editor, "production-contract-readonly-form"); editor.Close();
                });
                ((ButtonBase)page.FindName("EditProductionContract")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
            }
            catch (Exception ex) { failure = ex; }
            finally { foreach (var editor in owner.OwnedWindows.OfType<ProductionEditorWindow>().ToArray()) { editor.Owner = null; editor.Hide(); } owner.Close(); }
        };
        owner.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task DriveProductionEditor(Window owner, Func<ProductionEditorWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<ProductionEditorWindow>().Any()); var editor = owner.OwnedWindows.OfType<ProductionEditorWindow>().Single();
        try { await run(editor); } catch { editor.Owner = null; editor.Hide(); throw; }
    }
}
