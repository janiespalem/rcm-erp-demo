using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunTemplatesWorkflow()
    {
        var server = new NativeTemplateServer(); using var api = new CrmClient(new Uri("http://localhost/"), server);
        api.Login("technolog", "0000", default).GetAwaiter().GetResult();
        var main = new MainWindow(api); Exception? failure = null;
        main.Loaded += async (_, _) =>
        {
            try
            {
                ((ButtonBase)main.FindName("TemplatesNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var workspace = (TemplateWorkspace)main.FindName("TemplatesWorkspace");
                await Until(() => workspace.Model.Rows.Count == 1 && !workspace.Model.Busy); await Capture(main, "templates-sop-catalog");
                ((DataGrid)workspace.FindName("TemplateRows")).SelectedIndex = 0;
                var flow = DriveTemplateEditor(main, async editor =>
                {
                    ((TextBox)editor.FindName("TemplateName")).Text = "Synthetic native SOP";
                    editor.Model.Operations[0].Hours = "2,5"; editor.Model.Materials[0].Dimension = "20 x 30 x 500";
                    var tabs = (TabControl)editor.FindName("TemplateTabs"); tabs.SelectedIndex = 3;
                    ((ComboBox)editor.FindName("SopLibrary")).SelectedIndex = 0;
                    FindButton(editor, "Dodaj blok").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(2, editor.Model.Instructions.Count); await Capture(editor, "templates-sop-editor");
                    var dismiss = DismissNo("Niezapisany szablon SOP"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    main.Close(); FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.True(main.IsVisible); Assert.NotNull(api.Session);
                    server.LoseNextWrite = true; ((ButtonBase)editor.FindName("SaveTemplate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                    Assert.True(((TextBox)editor.FindName("TemplateName")).IsReadOnly); await Capture(editor, "templates-save-unknown");
                    ((ButtonBase)editor.FindName("RetryTemplate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Uncertain && !editor.Model.Busy && editor.Model.Committed); Assert.Equal(server.Bodies[0], server.Bodies[1]);
                    tabs.SelectedIndex = 5;
                    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
                    try
                    {
                        await File.WriteAllTextAsync(file, "%PDF-1.4 synthetic Windows drawing"); await editor.Model.SelectDrawing(file, default);
                        await File.WriteAllTextAsync(file, "%PDF-1.4 replaced source"); server.LoseNextWrite = true;
                        ((ButtonBase)editor.FindName("UploadDrawing")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                        ((ButtonBase)editor.FindName("RetryTemplate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => !editor.Model.Uncertain && !editor.Model.Busy && editor.Model.Current!.HasDrawing);
                        Assert.Equal(server.UploadIds[0], server.UploadIds[1]); Assert.Contains("Windows drawing", server.Bodies[^1]);
                        ((ButtonBase)editor.FindName("ReadDrawing")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        await Until(() => editor.Model.HasPreview && !editor.Model.Busy); await Capture(editor, "templates-drawing-review");
                        Assert.Equal("Synthetic native SOP", server.Template.Name); Assert.True(editor.Model.CanApply);
                        var yes = ConfirmTemplateApply(); ((ButtonBase)editor.FindName("ApplyDrawing")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await yes;
                        await Until(() => !editor.Model.Busy && editor.Model.Current!.Name == "Synthetic extracted part"); Assert.False(editor.Model.Dirty);
                    }
                    finally { File.Delete(file); }
                    editor.Close();
                });
                FindButton(workspace, "Otwórz szablon").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await flow;
                ((ButtonBase)main.FindName("ProjectsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => workspace.Model.IsProjects && workspace.Model.Rows.Count == 1 && !workspace.Model.Busy); await Capture(main, "templates-projects");
                server.Role = "biuro"; await api.Login("biuro", "0000", default);
                ((ButtonBase)main.FindName("TemplateCatalogNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => workspace.Model.Mode == TemplateWorkspaceMode.Catalog && workspace.Model.Rows.Count == 1 && !workspace.Model.Busy);
                Assert.Equal("Do wyceny", workspace.Model.Rows[0].Price); Assert.False(workspace.Model.CanCreate); await Capture(main, "templates-biuro-catalog");
            }
            catch (Exception ex) { failure = ex; }
            finally { foreach (var editor in main.OwnedWindows.OfType<TemplateEditorWindow>().ToArray()) { editor.Owner = null; editor.Hide(); } main.Close(); }
        };
        main.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task DriveTemplateEditor(Window owner, Func<TemplateEditorWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<TemplateEditorWindow>().Any());
        var editor = owner.OwnedWindows.OfType<TemplateEditorWindow>().Single();
        try { await run(editor); }
        catch { editor.Owner = null; editor.Hide(); throw; }
    }
    private static Task ConfirmTemplateApply() => Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var dialog = FindWindow("#32770", "Zastosuj odczyt");
            if (dialog != IntPtr.Zero) { SendMessage(dialog, 0x0111, new IntPtr(6), IntPtr.Zero); return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("Template drawing review confirmation did not appear.");
    });
}
