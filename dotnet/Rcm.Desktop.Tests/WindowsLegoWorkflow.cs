using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunLegoWorkflow()
    {
        var server = new NativeLegoServer(); using var api = LegoTests.Api(server).GetAwaiter().GetResult(); var store = new MemoryLegoCartStore();
        var main = new MainWindow(api, store); Exception? failure = null;
        main.Loaded += async (_, _) =>
        {
            try
            {
                ((ButtonBase)main.FindName("LegoNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var page = (LegoPage)main.FindName("LegoWorkspace"); await Until(() => page.Model.Series.Count == 7 && page.Model.CartLoaded && !page.IsWorking);
                Assert.Equal(9, page.Model.Products.Count); Assert.True(main.Model.IsLegoVisible); await Capture(main, "lego-catalog");
                var series = (ComboBox)page.FindName("LegoSeries"); series.SelectedValue = "lego60std";
                var shapes = (ComboBox)page.FindName("LegoShape"); var tabs = (TabControl)page.FindName("LegoTabs");
                page.Model.DimensionA = "7,2"; page.Model.DimensionB = "7,2"; page.Model.DimensionC = "7,2"; page.Model.Height = "1,2"; page.Model.Boxes = "2";
                foreach (var shape in new[] { "prosta", "L", "U", "boksy" })
                {
                    shapes.SelectedValue = shape; ((ButtonBase)page.FindName("CalculateLego")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => page.Model.HasResult && !page.IsWorking); Assert.Equal(shape, server.Input!.Shape);
                    var scene = (LegoViewport)page.FindName("LegoScene"); Assert.NotNull(scene.Scene); Assert.True(scene.Scene!.IsFrozen);
                    var viewport = scene.Children.OfType<Viewport3D>().Single(); var camera = (PerspectiveCamera)viewport.Camera; var before = camera.Position;
                    scene.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent }); Assert.NotEqual(before, camera.Position);
                    tabs.SelectedIndex = 0; await Capture(main, "lego-shape-" + shape);
                }
                tabs.SelectedIndex = 1; await Capture(main, "lego-native-plan-2d"); tabs.SelectedIndex = 2; await Capture(main, "lego-courses-corners");
                page.Model.WithArch = true; page.Model.ArchPrice = "0"; tabs.SelectedIndex = 0;
                ((ButtonBase)page.FindName("CalculateLego")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.HasResult && !page.IsWorking);
                Assert.True(page.Model.Result!.MissingArchPrice); Assert.Contains("Brak ceny łuków", page.Model.Warnings); await Capture(main, "lego-native-arch");
                ((ButtonBase)page.FindName("AddLegoResult")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Single(page.Model.Cart); Assert.False(page.Model.Cart[0].Entry.Complete);
                page.Model.SelectedProduct = page.Model.Products.Single(product => product.Key == "roadPlate");
                ((ButtonBase)page.FindName("AddLegoProduct")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); ((ButtonBase)page.FindName("AddLegoProduct")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(2, page.Model.Cart[1].Quantity); await Capture(main, "lego-local-cart");
                store.FailNextWrite = true; ((ButtonBase)page.FindName("SaveLegoCart")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.IsWorking && page.Model.CartStatus.Contains("Nie udało"));
                Assert.True(page.Model.CartDirty); Assert.Equal(2, page.Model.Cart.Count); await Capture(main, "lego-cart-write-retry");
                ((ButtonBase)page.FindName("SaveLegoCart")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.IsWorking && !page.Model.CartDirty); Assert.Equal(2, store.Users[101].Length);
                var dismiss = DismissNo("Dane LEGO"); main.Close(); await dismiss; Assert.True(main.IsVisible);
                dismiss = DismissNo("Dane LEGO"); FindButton(main, "Wyloguj").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await dismiss; Assert.NotNull(api.Session);
                server.LoseNext = true; ((ButtonBase)page.FindName("CalculateLego")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !page.IsWorking && page.Model.Status.Contains("Brak połączenia"));
                Assert.Equal("5,40", page.Model.DimensionA); ((ButtonBase)page.FindName("CalculateLego")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.HasResult && !page.IsWorking);
                api.Logout(); server.Actor = 202; await api.Login("biuro", "0000", default); ((ButtonBase)main.FindName("LegoNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.UserId == 202 && page.Model.CartLoaded && !page.IsWorking); Assert.Empty(page.Model.Cart);
                api.Logout(); server.Actor = 101; await api.Login("biuro", "0000", default); ((ButtonBase)main.FindName("LegoNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => page.Model.UserId == 101 && !page.IsWorking); Assert.Equal(2, page.Model.Cart.Count);
                server.Role = "ceo"; await api.Login("ceo", "0000", default); Assert.False(main.Model.IsLegoVisible); Assert.Equal(Visibility.Collapsed, ((ButtonBase)main.FindName("LegoNav")).Visibility);
            }
            catch (Exception ex) { failure = ex; }
            finally { ((LegoPage)main.FindName("LegoWorkspace")).Clear(); main.Close(); }
        };
        main.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
