using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static readonly List<string> ViewportFailures = [];

    private static async Task VerifyViewport(Window window, string scenario)
    {
        if (scenario == "shift-reports-a4-preview") return;
        var state = window.WindowState;
        var width = window.Width;
        var height = window.Height;
        try
        {
            window.WindowState = WindowState.Normal;
            // These are the usable DIP budgets, not a claim to change the monitor's hardware DPI.
            foreach (var (pixelsWide, pixelsHigh, scale) in new[] { (1366d, 768d, 1d), (1366d, 768d, 1.25d), (1920d, 1080d, 1.5d) })
            {
                window.Width = Math.Min(width, pixelsWide / scale);
                window.Height = Math.Min(height, (pixelsHigh - 48) / scale);
                // WPF UI expander transitions continue after ApplicationIdle during responsive collapse.
                await Task.Delay(300);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SaveSnapshot(window, $"{scenario}-{pixelsWide:0}x{pixelsHigh:0}-{scale * 100:0}pct");
                try
                {
                    Assert.True(window.ActualWidth <= pixelsWide / scale + 1 && window.ActualHeight <= (pixelsHigh - 48) / scale + 1,
                        $"{scenario}: window {window.ActualWidth}×{window.ActualHeight} exceeds the {pixelsWide}×{pixelsHigh}/{scale:P0} usable budget.");
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    Assert.True(content.ActualHeight > 0, scenario);
                    foreach (var button in VisualDescendants<Button>(content).Where(b => b.IsVisible && !InsideScrollViewer(b, content)))
                    {
                        var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Top >= -1 && bounds.Bottom <= content.ActualHeight + 1 &&
                                    bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1,
                            $"{scenario} at {pixelsWide}×{pixelsHigh}/{scale:P0}: {button.Content} clipped ({bounds}, content {content.RenderSize}).");
                    }
                    foreach (var cart in VisualDescendants<DataGrid>(content).Where(g => g.IsVisible && g.Name == "LegoCart" && g.Items.Count > 0))
                    {
                        var body = VisualDescendants<ScrollViewer>(content).Single(s => s.Name == "LegoCartBody");
                        var bounds = cart.TransformToAncestor(body).TransformBounds(new Rect(cart.RenderSize));
                        Assert.True(cart.EnableRowVirtualization && cart.EnableColumnVirtualization, $"{scenario}: cart virtualization");
                        Assert.True(bounds.Top >= 0 && bounds.Top + cart.ColumnHeaderHeight + 32 <= body.ViewportHeight,
                            $"{scenario} at {scale:P0}: cart has no fully visible first row (top {bounds.Top}, viewport {body.ViewportHeight}).");
                    }
                    foreach (var grid in VisualDescendants<DataGrid>(content).Where(g => g.IsVisible && !InsideScrollViewer(g, content)))
                    {
                        Assert.True(grid.EnableRowVirtualization && grid.EnableColumnVirtualization, $"{scenario}: {grid.Name} virtualization");
                        if (grid.Items.Count > 0)
                            Assert.True(grid.ActualHeight >= grid.ColumnHeaderHeight + 32,
                                $"{scenario} at {scale:P0}: {grid.Name} has no readable row ({grid.ActualHeight}).");
                    }
                }
                catch (Xunit.Sdk.XunitException error)
                {
                    ViewportFailures.Add(error.Message);
                    Console.Error.WriteLine(error.Message);
                }
            }
            if (window is LoginWindow login)
            {
                var username = (TextBox)login.FindName("Username");
                username.Focus();
                Assert.True(username.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(((PasswordBox)login.FindName("Password")).IsKeyboardFocusWithin);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Viewport failure in {scenario}: {error}");
            throw;
        }
        finally
        {
            window.Width = width;
            window.Height = height;
            window.WindowState = state;
            await Task.Delay(300);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static bool InsideScrollViewer(DependencyObject element, DependencyObject root)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null && parent != root; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer) return true;
        return false;
    }
}
