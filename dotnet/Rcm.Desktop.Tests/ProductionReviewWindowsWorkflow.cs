using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunProductionReviewWorkflow()
    {
        var server = new ProductionReviewServer(); server.Source.Report = server.Source.Report with { Fields = NativeShiftReportsServer.CompleteFields, ValidationErrors = [] };
        using var api = ProductionReviewTests.Login(server).GetAwaiter().GetResult(); var page = new ProductionPage(); page.SetClient(api);
        var owner = new Window { Title = "Synthetic production report workflow", Width = 1280, Height = 800, Style = (Style)Application.Current.FindResource("WindowStyle"), Content = page }; Exception? failure = null;
        owner.Loaded += async (_, _) =>
        {
            try
            {
                await page.Activate(); ((DataGrid)page.FindName("ProductionContracts")).SelectedIndex = 0;
                await Until(() => page.Model.Detail is not null && !page.Model.DetailBusy);
                var workerModel = new ShiftReportsEditorViewModel(server.Source.Report, server.Source.Features);
                var worker = new ShiftReportsEditorWindow(api, workerModel) { Owner = owner };
                var flow = DriveShiftReportsEditor(owner, async editor =>
                {
                    await Until(() => editor.Model.Production.Initialized && !editor.Model.Production.Busy);
                    Assert.Equal(Visibility.Visible, ((Border)editor.FindName("ReportContractSummary")).Visibility);
                    Assert.Equal("Bez przypisanego kontraktu", ((TextBlock)editor.FindName("ReportContractName")).Text);
                    ((ButtonBase)editor.FindName("OpenReportContract")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Same(editor.FindName("ReportProductionTab"), ((TabControl)editor.FindName("ShiftReportTabs")).SelectedItem);
                    editor.Model.Field("remarks").Value = "Retained source text through allocation";
                    ((ComboBox)editor.FindName("ReportContractSelection")).SelectedIndex = 0;
                    Assert.False(editor.Model.CanFinalize); server.LoseNext = true;
                    ((ButtonBase)editor.FindName("LinkProductionReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Production.Uncertain && !editor.Model.Production.Busy);
                    Assert.Equal("Retained source text through allocation", editor.Model.Field("remarks").Value); Assert.False(editor.Model.CanEdit);
                    var dismiss = DismissNo("Niezapisany raport zmiany"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    await Capture(editor, "production-report-link-unknown-laptop");
                    ((ButtonBase)editor.FindName("RetryProductionReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Production.Uncertain && !editor.Model.Production.Busy && editor.Model.Production.State?.Link is not null);
                    Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Equal("Przekaż do sprawdzenia", ((Button)editor.FindName("FinalizeShiftReport")).Content);
                    Assert.Equal(editor.Model.Production.State!.Link!.ContractName, ((TextBlock)editor.FindName("ReportContractName")).Text);
                    ((ButtonBase)editor.FindName("ReturnToReportForm")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Same(editor.FindName("ReportFormTab"), ((TabControl)editor.FindName("ShiftReportTabs")).SelectedItem);
                    Assert.Equal("Retained source text through allocation", editor.Model.Field("remarks").Value);
                    ((ButtonBase)editor.FindName("SaveShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Busy && editor.Model.Current?.Version == 2 && editor.Model.Production.State?.Report.Version == 2);
                    var confirm = ConfirmShiftDialog("Zakończenie raportu"); ((ButtonBase)editor.FindName("FinalizeShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await confirm;
                    await Until(() => !editor.Model.Busy && editor.Model.Production.State?.State == "pending"); Assert.False(editor.Model.Production.CanReview); editor.Close();
                });
                worker.ShowDialog(); await flow;
                server.Role = "biuro"; server.Reviewer = true; await api.Login("biuro", "0000", default); await page.Activate();
                var tabs = (TabControl)page.FindName("ProductionTabs"); tabs.SelectedIndex = 3;
                ((ButtonBase)page.FindName("LoadProductionReviewQueue")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => page.Model.Reports.Queue.Count == 1 && !page.Model.Reports.Busy);
                ((DataGrid)page.FindName("ProductionReviewQueue")).SelectedIndex = 0; await Capture(owner, "production-assigned-review-queue-laptop");
                flow = DriveShiftReportsEditor(owner, async editor =>
                {
                    await Until(() => editor.Model.Production.Initialized && !editor.Model.Production.Busy);
                    Assert.True(editor.Model.ReadOnly); Assert.False(editor.Model.CanSave); Assert.False(editor.Model.Production.CanChoose); Assert.True(editor.Model.Production.CanReview);
                    Assert.Equal("Retained source text through allocation", editor.Model.Field("remarks").Value);
                    Assert.Equal("Do sprawdzenia", ((TextBlock)editor.FindName("ReportReviewState")).Text);
                    ((ButtonBase)editor.FindName("OpenReportContract")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Same(editor.FindName("ReportProductionTab"), ((TabControl)editor.FindName("ShiftReportTabs")).SelectedItem);
                    ((TextBox)editor.FindName("ReportReviewReason")).Text = "Checked against shift record";
                    await Capture(editor, "production-report-review-laptop"); server.LoseNext = true;
                    var confirm = ConfirmShiftDialog("Przyjęcie raportu"); ((ButtonBase)editor.FindName("AcceptProductionReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await confirm;
                    await Until(() => editor.Model.Production.Uncertain && !editor.Model.Production.Busy); Assert.Equal("Checked against shift record", editor.Model.Production.ReviewReason);
                    ((ButtonBase)editor.FindName("RetryProductionReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Production.Uncertain && !editor.Model.Production.Busy && editor.Model.Production.State?.State == "accepted"); Assert.Equal(server.Bodies[^2], server.Bodies[^1]); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenProductionReview")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                await Until(() => !page.Model.Reports.Busy && page.Model.Reports.Queue.Count == 0 && page.Model.Reports.Operations?.Reports == 1);
                tabs.SelectedIndex = 2; Assert.Equal(10, page.Model.Reports.Operations!.Poured); await Capture(owner, "production-accepted-operations-laptop");
                server.Role = "produkcja"; server.Reviewer = false; await api.Login("produkcja", "0000", default); await page.Activate();
                Assert.False(page.Model.Reports.CanReview); Assert.Equal(Visibility.Collapsed, ((TabItem)tabs.Items[3]).Visibility);
                ((ButtonBase)page.FindName("LoadContractReports")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.Reports.Reports.Count == 1 && !page.Model.Reports.Busy);
                ((DataGrid)page.FindName("ContractReports")).SelectedIndex = 0;
                flow = DriveShiftReportsEditor(owner, async editor =>
                {
                    await Until(() => editor.Model.Production.Initialized && !editor.Model.Production.Busy);
                    Assert.Equal("accepted", editor.Model.Production.State!.State); Assert.False(editor.Model.Production.CanReview);
                    ((TabControl)editor.FindName("ShiftReportTabs")).SelectedIndex = 1; ((ButtonBase)editor.FindName("StartShiftCorrection")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    editor.Model.Field("poured").Value = "12"; ((TextBox)editor.FindName("ShiftCorrectionReason")).Text = "Verified correction after acceptance";
                    ((ButtonBase)editor.FindName("CorrectShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Busy && editor.Model.Production.State?.State == "pending" && editor.Model.Current!.Status == "corrected"); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenContractReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                await Until(() => !page.Model.Reports.Busy && page.Model.Reports.Operations?.Reports == 0); Assert.Equal(0, page.Model.Reports.Operations!.Poured);
                server.Role = "ceo"; await api.Login("ceo", "0000", default); await page.Activate();
                ((ButtonBase)page.FindName("LoadContractReports")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.Reports.Reports.Count == 1 && !page.Model.Reports.Busy);
                ((DataGrid)page.FindName("ContractReports")).SelectedIndex = 0;
                flow = DriveShiftReportsEditor(owner, async editor =>
                {
                    await Until(() => editor.Model.Production.Initialized && !editor.Model.Production.Busy);
                    Assert.True(editor.Model.ReadOnly); Assert.False(editor.Model.CanStartCorrection); Assert.False(editor.Model.Production.CanChoose); Assert.False(editor.Model.Production.CanReview);
                    ((TabControl)editor.FindName("ShiftReportTabs")).SelectedIndex = 5; Assert.False(((ButtonBase)editor.FindName("AcceptProductionReport")).IsEnabled); Assert.False(((ButtonBase)editor.FindName("LinkProductionReport")).IsEnabled);
                    await Capture(editor, "production-report-module-readonly-laptop"); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenContractReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
            }
            catch (Exception ex) { failure = ex; }
            finally { foreach (var editor in owner.OwnedWindows.OfType<ShiftReportsEditorWindow>().ToArray()) { editor.Owner = null; editor.Hide(); } owner.Close(); }
        };
        owner.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
