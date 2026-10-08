using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunShiftReportsWorkflow()
    {
        var server = new NativeShiftReportsServer { DefaultShift = "II" }; using var api = ShiftReportsTests.Api(server).GetAwaiter().GetResult();
        var updates = new RunningAppUpdateClient();
        var main = new MainWindow(api, updateClient: updates); Exception? failure = null;
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("updateTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Assert.Equal(TimeSpan.FromMinutes(30), timer.Interval);
        timer.Interval = TimeSpan.FromMilliseconds(40);
        main.Loaded += async (_, _) =>
        {
            try
            {
                var page = (ShiftReportsPage)main.FindName("ShiftReportsWorkspace");
                await Until(() => main.Model.IsShiftReportsVisible && page.Model.Rows.Count == 1 && !page.Model.Busy);
                Assert.Equal("II", ((ComboBox)page.FindName("ShiftReportShift")).SelectedItem);
                Assert.Equal(server.Report.ReportDate.ToDateTime(TimeOnly.MinValue), ((DatePicker)page.FindName("ShiftReportDay")).SelectedDate);
                Assert.True(((ButtonBase)page.FindName("OpenShiftDate")).IsEnabled);
                await Until(() => main.Updates.Ready && !main.Updates.Busy);
                Assert.Equal(Visibility.Visible, ((Button)main.FindName("InstallUpdate")).Visibility);
                Assert.Equal(MainSection.ShiftReports, main.Model.Section); await Capture(main, "shift-reports-production-today");
                ((DataGrid)page.FindName("ShiftReportRows")).SelectedIndex = 0;
                var flow = DriveShiftReportsEditor(main, async editor =>
                {
                    Assert.True(editor.Model.CanEdit); Assert.Equal(9, editor.Model.Checks.Count);
                    Assert.Equal(Visibility.Collapsed, ((Border)editor.FindName("ReportContractSummary")).Visibility);
                    ShiftReportsTests.Complete(editor.Model); editor.Model.Field("reference").Value = "SYNTHETIC FREE BATCH / 17";
                    editor.Model.Field("remarks").Value = "Synthetic handover note";
                    ((ButtonBase)main.FindName("InstallUpdate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(0, updates.Applies); Assert.True(main.Updates.Ready);
                    Assert.Equal("Synthetic handover note", editor.Model.Field("remarks").Value);
                    Assert.Contains("Zapisz dane", ((TextBlock)main.FindName("UpdateStatus")).Text);
                    await Capture(editor, "shift-reports-form");
                    var dismiss = DismissNo("Niezapisany raport zmiany"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    main.Close(); FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.True(main.IsVisible); Assert.NotNull(api.Session);
                    server.LoseNextWrite = true; ((ButtonBase)editor.FindName("SaveShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy); Assert.True(editor.Model.ReadOnly); await Capture(editor, "shift-reports-save-unknown");
                    ((ButtonBase)editor.FindName("RetryShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Committed && !editor.Model.Uncertain && !editor.Model.Busy); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.False(editor.Model.CanDiscard);
                    var confirm = ConfirmShiftDialog("Zakończenie raportu"); ((ButtonBase)editor.FindName("FinalizeShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await confirm;
                    await Until(() => editor.Model.Current!.Status == "finalized" && !editor.Model.Busy);
                    var tabs = (TabControl)editor.FindName("ShiftReportTabs"); tabs.SelectedIndex = 1;
                    ((ButtonBase)editor.FindName("StartShiftCorrection")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    ((TextBox)editor.FindName("ShiftCorrectionReason")).Text = "Synthetic confirmed recount"; editor.Model.Field("poured").Value = "22";
                    await Capture(editor, "shift-reports-correction");
                    dismiss = DismissNo("Niezapisany raport zmiany"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    ((ButtonBase)editor.FindName("CorrectShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Current!.Status == "corrected" && !editor.Model.Busy); Assert.Equal(22, server.Report.Fields.Poured); Assert.Equal(1, server.Report.CorrectionCount);
                    tabs.SelectedIndex = 3; FindButton(editor, "Wczytaj historię").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Audit.Count == 1 && !editor.Model.Busy); await Capture(editor, "shift-reports-audit");
                    tabs.SelectedIndex = 4; FindButton(editor, "Przygotuj podgląd A4").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.NotNull(((FlowDocumentScrollViewer)editor.FindName("ShiftPrintPreview")).Document); await Capture(editor, "shift-reports-a4-preview"); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                ((ButtonBase)main.FindName("InstallUpdate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => !main.Updates.Busy);
                Assert.Equal(1, updates.Applies);
                Assert.NotNull(api.Session);
                server.Role = "ceo"; await api.Login("ceo", "0000", default);
                ((ButtonBase)main.FindName("ShiftReportsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => page.Model.Rows.Count == 1 && !page.Model.Busy && !page.Model.Features.Write);
                ((DataGrid)page.FindName("ShiftReportRows")).SelectedIndex = 0;
                var readFlow = DriveShiftReportsEditor(main, async editor =>
                {
                    Assert.True(editor.Model.ReadOnly); Assert.True(editor.Model.CanAdminDelete); Assert.False(editor.Model.CanStartCorrection);
                    await Capture(editor, "shift-reports-ceo-readonly"); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenShiftReport")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await readFlow;
            }
            catch (Exception ex) { failure = ex; }
            finally { foreach (var editor in main.OwnedWindows.OfType<ShiftReportsEditorWindow>().ToArray()) { editor.Owner = null; editor.Hide(); } main.Close(); }
        };
        main.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(updates.Disposed);
    }
    private sealed class RunningAppUpdateClient : IBackgroundUpdateClient
    {
        public bool IsInstalled => true;
        public int Applies { get; private set; }
        public bool Disposed { get; private set; }
        public Task<string?> Check(CancellationToken ct) => Task.FromResult<string?>(Applies == 0 ? "9.0.0" : null);
        public Task Download(Action<int> progress, CancellationToken ct) { progress(100); return Task.CompletedTask; }
        public Task Apply(CancellationToken ct) { Applies++; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
    private static async Task DriveShiftReportsEditor(Window owner, Func<ShiftReportsEditorWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<ShiftReportsEditorWindow>().Any()); var editor = owner.OwnedWindows.OfType<ShiftReportsEditorWindow>().Single();
        try { await run(editor); } catch { editor.Owner = null; editor.Hide(); throw; }
    }
    private static Task ConfirmShiftDialog(string title) => Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var dialog = FindWindow("#32770", title); if (dialog != IntPtr.Zero) { SendMessage(dialog, 0x0111, new IntPtr(6), IntPtr.Zero); return; }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Shift report confirmation '{title}' did not appear.");
    });
}
