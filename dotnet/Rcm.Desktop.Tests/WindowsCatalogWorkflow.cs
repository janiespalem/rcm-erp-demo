using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunCatalogWorkflow()
    {
        var server = new NativeCatalogServer();
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        api.Login("technolog", "0000", default).GetAwaiter().GetResult();
        var main = new MainWindow(api);
        Exception? failure = null;
        main.Loaded += async (_, _) =>
        {
            try
            {
                Assert.True(main.Model.CanManageCatalog);
                ((ButtonBase)main.FindName("MaterialsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var materials = (CatalogPage)main.FindName("MaterialCatalog");
                await Until(() => materials.Model.Rows.Count == 1 && !materials.Model.Busy);
                await Capture(main, "catalog-materials");
                var materialFlow = DriveCatalogEditor(main, async editor =>
                {
                    editor.Model.Name = "Synthetic native steel";
                    editor.Model.Group = "blacha"; editor.Model.Rate = "6,25";
                    editor.Model.Notes = "Synthetic material dimensions and use";
                    await Capture(editor, "catalog-material-editor");
                    var dismiss = DismissNo("Niezapisane dane katalogu"); editor.Close(); await dismiss;
                    Assert.True(editor.IsVisible); Assert.Equal("Synthetic native steel", editor.Model.Name);
                    main.Close(); FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(main.IsVisible); Assert.NotNull(api.Session); Assert.True(editor.Model.Dirty);
                    server.LoseNextWrite = true;
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                    Assert.False(((TextBox)editor.FindName("CatalogName")).IsEnabled);
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(materials, "+ Dodaj pozycję").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await materialFlow;
                await Until(() => materials.Model.Rows.Any(row => row.Name == "Synthetic native steel") && !materials.Model.Busy);
                Assert.Equal(server.WriteBodies[0], server.WriteBodies[1]); Assert.Single(server.Receipts);
                ((DataGrid)materials.FindName("CatalogRows")).SelectedIndex = 0;
                var archiveFlow = DriveCatalogEditor(main, async editor =>
                {
                    Assert.True(editor.Model.IsRemoval);
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(materials, "Archiwizuj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await archiveFlow;
                await Until(() => materials.Model.Rows.Count == 0 && !materials.Model.Busy);
                materials.Model.IncludeArchived = true;
                await materials.Activate();
                Assert.False(Assert.Single(materials.Model.Rows).IsActive);
                ((DataGrid)materials.FindName("CatalogRows")).SelectedIndex = 0;
                var restoreFlow = DriveCatalogEditor(main, async editor =>
                {
                    editor.Model.Active = true;
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(materials, "Edytuj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await restoreFlow;
                await Until(() => server.Material.IsActive && !materials.Model.Busy);
                Assert.Equal("Synthetic material dimensions and use", server.Material.Notes);
                ((ButtonBase)main.FindName("OperationsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var operations = (CatalogPage)main.FindName("OperationCatalog");
                await Until(() => operations.Model.Rows.Count == 1 && !operations.Model.Busy);
                await Capture(main, "catalog-operations");
                ((DataGrid)operations.FindName("CatalogRows")).SelectedIndex = 0;
                var operationFlow = DriveCatalogEditor(main, async editor =>
                {
                    Assert.Equal("synthetic welding frame", editor.Model.Formula);
                    editor.Model.Name = "Synthetic frame welding"; editor.Model.Group = "Spawalnia";
                    editor.Model.Rate = "125,50"; editor.Model.Formula = "rama mig mag naprawa";
                    await Capture(editor, "catalog-operation-editor");
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(operations, "Edytuj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await operationFlow;
                await Until(() => operations.Model.Rows.Any(row => row.Name == "Synthetic frame welding") && !operations.Model.Busy);
                Assert.Equal(125.5, server.Operation.DefaultRate); Assert.Equal("rama mig mag naprawa", server.Operation.Formula);
                ((DataGrid)operations.FindName("CatalogRows")).SelectedIndex = 0;
                var deleteFlow = DriveCatalogEditor(main, async editor =>
                {
                    Assert.True(editor.Model.IsRemoval);
                    ((ButtonBase)editor.FindName("SaveCatalog")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.IsVisible);
                });
                FindButton(operations, "Usuń").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await deleteFlow;
                await Until(() => operations.Model.Rows.Count == 0 && !operations.Model.Busy);
                Assert.True(server.OperationDeleted);
                server.Role = "ceo"; await api.Login("ceo", "0000", default);
                Assert.False(main.Model.CanManageCatalog);
                Assert.Equal(Visibility.Collapsed, ((Button)main.FindName("MaterialsNav")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)main.FindName("OperationsNav")).Visibility);
            }
            catch (Exception ex) { failure = ex; }
            finally { main.Close(); }
        };
        main.ShowDialog();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task DriveCatalogEditor(Window owner, Func<CatalogEditorWindow, Task> run)
    {
        await Until(() => owner.OwnedWindows.OfType<CatalogEditorWindow>().Any());
        await run(owner.OwnedWindows.OfType<CatalogEditorWindow>().Single());
    }
}
