using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunInsightsWorkflow()
    {
        foreach (var role in new[] { "ceo", "biuro" })
        {
            var server = new InsightsServer { Role = role }; using var api = new CrmClient(new Uri("http://localhost/"), server);
            Task.Run(() => api.Login(role,"0000",default)).GetAwaiter().GetResult(); var window = new MainWindow(api); Exception? failure = null;
            window.Loaded += async (_, _) =>
            {
                try
                {
                    ((ButtonBase)window.FindName("InsightsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var page = (InsightsPage)window.FindName("InsightsWorkspace"); await Until(() => page.Model.Rows.Count > 0 && !page.Model.Busy);
                    Assert.Equal(role == "biuro" ? 1 : 6, page.Model.Choices.Count);
                    var selector = (ComboBox)page.FindName("ReportSelector");
                    foreach (var choice in page.Model.Choices.ToArray())
                    {
                        selector.SelectedValue = choice.Section;
                        await Until(() => !page.Model.Busy && page.Model.Rows.Count > 0);
                        Assert.NotEmpty(((DataGrid)page.FindName("InsightList")).Columns);
                        if (choice.Section == InsightSection.Profitability) Assert.Equal("25,00%", page.Model.Rows.Single()["pct"]);
                        await Capture(window,"insights-" + choice.Section.ToString().ToLowerInvariant() + "-" + role);
                    }
                    if (role == "ceo")
                    {
                        selector.SelectedValue = InsightSection.Production; await Until(() => !page.Model.Busy && page.Model.Rows.Count > 0);
                        ((TextBox)page.FindName("ReportSearch")).Text = "%_ Żółć"; await Task.Delay(400); await Until(() => !page.Model.Busy);
                        Assert.Contains(server.Queries,q => q.Contains("%25_"));
                        ((ButtonBase)page.FindName("NextInsights")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.Model.Busy && page.Model.Page == 2);
                        var target = Path.Combine(Path.GetTempPath(),"rcm-insights-ui-" + Guid.NewGuid().ToString("N") + ".xlsx");
                        try
                        {
                            await page.ExportTo(target); Assert.True(File.Exists(target)); Assert.False(page.Model.Exporting);
                            var saved = await File.ReadAllBytesAsync(target); server.WaitForExportCancellation = true;
                            var downloading = page.ExportTo(target); await Until(() => page.Model.Exporting); page.CancelExport(); await downloading;
                            Assert.Equal(saved, await File.ReadAllBytesAsync(target)); Assert.Equal("Zapis anulowano.", page.Model.Status);
                        }
                        finally { File.Delete(target); }
                        selector.SelectedValue = InsightSection.Analytics; await Until(() => !page.Model.Busy && page.Model.Rows.Count > 0);
                        window.WindowState = WindowState.Normal; window.Width = 940; window.Height = 600;
                        await Capture(window,"insights-analytics-compact");
                    }
                    else
                    {
                        Assert.False(page.Model.CanExport); Assert.Equal(InsightSection.ServiceHistory,page.Model.Section);
                        await Until(() => window.Model.PendingQuestions == 3); Assert.Equal("Pytania · 3",window.Model.QuestionsNavTitle);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            window.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
