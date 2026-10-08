using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunTetrapodDeliveryWorkflow()
    {
        var server = new TetrapodHistoryServer(); using var api = new CrmClient(new("http://localhost/"), server);
        api.Login("biuro", "0000", default).GetAwaiter().GetResult(); var store = new MemoryTetrapodDeliveryStore();
        var main = new MainWindow(api); var page = (TetrapodPage)main.FindName("Tetrapod"); page.SetClient(api, store); Exception? failure = null;
        main.Loaded += async (_, _) =>
        {
            try
            {
                ((ButtonBase)main.FindName("TetrapodNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => page.Model.HistoryLoaded && !page.Model.IsCalculating);
                ((TextBox)page.FindName("NewDelivery6")).Text = "100,25"; ((TextBox)page.FindName("NewDelivery12")).Text = "12";
                ((ButtonBase)page.FindName("AddDelivery")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var row = Assert.Single(page.Model.Deliveries); Assert.NotEmpty(row.Date); Assert.NotEmpty(row.Weekday); Assert.NotEmpty(row.Time);
                store.FailNextWrite = true; ((ButtonBase)page.FindName("SaveHistory")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => !page.Model.IsCalculating && page.Model.DeliveryStatus.Contains("Nie udało")); Assert.True(page.Model.HistoryDirty); await Capture(main, "tetrapod-delivery-save-retry");
                var dismiss = DismissNo("Dane kalkulatora"); main.Close(); await dismiss; Assert.True(main.IsVisible); Assert.Single(page.Model.Deliveries);
                dismiss = DismissNo("Dane kalkulatora"); FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await dismiss; Assert.NotNull(api.Session);
                ((ButtonBase)page.FindName("SaveHistory")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.Model.IsCalculating && !page.Model.HistoryDirty);
                Assert.Single(store.Users[101]); ((TextBox)page.FindName("Planned")).Text = "10";
                ((ButtonBase)page.FindName("Calculate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.HasResult && !page.Model.IsCalculating);
                Assert.Equal(100.25, server.Input!.DeliveriesKg![6]); Assert.Equal(12, server.Input.DeliveriesKg[12]); Assert.Equal(0, server.Input.DeliveriesKg[16]); await Capture(main, "tetrapod-delivery-history");
                await api.LogoutAsync(default); server.Actor = 202; await api.Login("biuro", "0000", default); await Until(() => page.Model.UserId == 202 && page.Model.HistoryLoaded && !page.Model.IsCalculating);
                Assert.Empty(page.Model.Deliveries); Assert.Equal("", page.Model.Planned); Assert.True(page.Model.Dirty);
                await api.LogoutAsync(default); server.Actor = 101; await api.Login("biuro", "0000", default); await Until(() => page.Model.UserId == 101 && !page.Model.IsCalculating);
                Assert.Single(page.Model.Deliveries); Assert.Equal("10", page.Model.Planned);
                page.Model.Planned = "";
                await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var history = (DataGrid)page.FindName("DeliveryHistory"); history.ScrollIntoView(page.Model.Deliveries[0]); history.UpdateLayout();
                var remove = DeliveryButtons(history).Single(button => Equals(button.Content, "Usuń"));
                var confirm = ConfirmDeliveryRemoval(); remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await confirm; Assert.Empty(page.Model.Deliveries);
                ((ButtonBase)page.FindName("SaveHistory")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.Model.IsCalculating && !page.Model.HistoryDirty); Assert.Empty(store.Users[101]);
                server.Role = "ceo"; await api.Login("ceo", "0000", default); Assert.False(page.Model.CanManageDeliveries); Assert.Equal(Visibility.Collapsed, ((ButtonBase)main.FindName("TetrapodNav")).Visibility);
            }
            catch (Exception ex) { failure = ex; }
            finally { page.Clear(); main.Close(); }
        };
        main.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static IEnumerable<Button> DeliveryButtons(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is Button button) yield return button;
            foreach (var nested in DeliveryButtons(child)) yield return nested;
        }
    }
    private static Task ConfirmDeliveryRemoval() => Task.Run(async () =>
    {
        for (var i = 0; i < 100; i++)
        {
            var dialog = FindWindow("#32770", "Usuń dostawę");
            if (dialog != IntPtr.Zero) { SendMessage(dialog, 0x0111, new IntPtr(6), IntPtr.Zero); return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("Delivery removal confirmation not found.");
    });
    private sealed class TetrapodHistoryServer : HttpMessageHandler
    {
        internal long Actor = 101;
        internal string Role = "biuro";
        internal TetrapodInput? Input;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-tetrapod-history" });
            if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(Actor, "Synthetic steel delivery user", null, null, Role));
            if (path.EndsWith("/orders/features")) return EditorTests.Reply(new OrderFeatures(false));
            if (path.EndsWith("/shift-reports/features")) return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures);
            if (path.EndsWith("/calculators/tetrapod")) { Input = await request.Content!.ReadFromJsonAsync<TetrapodInput>(ct); return EditorTests.Reply(TetrapodViewModelTests.Result()); }
            return EditorTests.Reply(new ApiError(404, "Synthetic unavailable module"), HttpStatusCode.NotFound);
        }
    }
}
