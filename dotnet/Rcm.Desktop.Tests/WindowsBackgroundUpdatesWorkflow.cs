using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunBackgroundUpdatesWorkflow()
    {
        var server = new NativeShiftReportsServer(); using var api = ShiftReportsTests.Api(server).GetAwaiter().GetResult();
        var client = new WorkflowUpdateClient(); var main = new MainWindow(api, updateClient: client);
        var updateTimer = UpdateField<DispatcherTimer>(main, "updateTimer"); var restartTimer = UpdateField<DispatcherTimer>(main, "restartTimer");
        Assert.Equal(TimeSpan.FromMinutes(30), updateTimer.Interval); Assert.Equal(TimeSpan.FromSeconds(1), restartTimer.Interval);
        Exception? failure = null;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => main.Model.IsShiftReportsVisible);
                updateTimer.Interval = TimeSpan.FromMilliseconds(25); restartTimer.Interval = TimeSpan.FromMilliseconds(25);
                await Until(() => client.Checks >= 2 && main.Updates.Ready && !main.Updates.Busy);
                Assert.Equal(1, client.Downloads); Assert.Equal("0.4.0", main.Updates.Version);
                ExpireUpdateDeadline(main);
                await Task.Delay(100); Assert.False(api.RememberMe); Assert.False(main.Updates.RestartPending); Assert.Equal(0, client.Applies); Assert.True(main.IsEnabled);
                await api.LoginWithPassword("synthetic.user", "synthetic-password", default, true); Assert.True(api.RememberMe); var session = api.Session;
                SetUpdateField(main, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); await Until(() => main.Updates.RestartPending);
                var deadline = Assert.IsType<DateTimeOffset>(UpdateField<DateTimeOffset?>(main, "restartAt")); Assert.InRange((deadline - DateTimeOffset.UtcNow).TotalSeconds, 25, 31); Assert.Equal(0, client.Applies);
                await Capture(main, "background-update-idle-countdown");
                SetUpdateField(main, "lastPointer", Mouse.GetPosition(main));
                main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(main.Updates.RestartPending); Assert.Null(UpdateField<DateTimeOffset?>(main, "restartAt"));
                SetUpdateField(main, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); await Until(() => main.Updates.RestartPending);
                SetUpdateField(main, "lastPointer", Mouse.GetPosition(main));
                main.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                Assert.True(main.Updates.RestartPending); Assert.NotNull(UpdateField<DateTimeOffset?>(main, "restartAt"));
                SetUpdateField(main, "lastPointer", new Point(-10000, -10000));
                main.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                Assert.False(main.Updates.RestartPending); Assert.Null(UpdateField<DateTimeOffset?>(main, "restartAt"));
                SetUpdateField(main, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); await Until(() => main.Updates.RestartPending);
                ((ButtonBase)main.FindName("PostponeUpdate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(main.Updates.RestartPending); Assert.InRange((UpdateField<DateTimeOffset>(main, "postponeUntil") - DateTimeOffset.UtcNow).TotalMinutes, 59, 61);
                SetUpdateField(main, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); await Task.Delay(100); Assert.False(main.Updates.RestartPending); Assert.Equal(0, client.Applies);
                SetUpdateField(main, "postponeUntil", DateTimeOffset.UtcNow.AddSeconds(-1));
                var tetrapod = ((TetrapodPage)main.FindName("Tetrapod")).Model; tetrapod.Planned = "7";
                ExpireUpdateDeadline(main); await Task.Delay(100); Assert.Equal("7", tetrapod.Planned); Assert.False(main.Updates.RestartPending); Assert.Equal(0, client.Applies);
                tetrapod.Planned = ""; tetrapod.IsCalculating = true;
                ExpireUpdateDeadline(main); await Task.Delay(100); Assert.False(main.Updates.RestartPending); Assert.Equal(0, client.Applies); tetrapod.IsCalculating = false;
                var owned = new Window { Style = (Style)Application.Current.FindResource("WindowStyle"), Owner = main, Title = "Synthetic owned update guard", Width = 300, Height = 180 };
                try { owned.Show(); ExpireUpdateDeadline(main); await Task.Delay(100); Assert.False(main.Updates.RestartPending); Assert.Equal(0, client.Applies); }
                finally { owned.Close(); }
                SetUpdateField(main, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); await Until(() => main.Updates.RestartPending); Assert.Equal(0, client.Applies);
                SetUpdateField<DateTimeOffset?>(main, "restartAt", DateTimeOffset.UtcNow.AddSeconds(-1));
                await Until(() => client.Applies == 1 && main.Updates.Busy);
                Assert.False(main.IsEnabled); Assert.True(api.RememberMe); Assert.Same(session, api.Session); Assert.False(main.Updates.RestartPending);
                await Task.Delay(100); Assert.Equal(1, client.Applies); Assert.False(main.IsEnabled); Assert.False(client.Disposed);
                client.CompleteApply(); await Until(() => !main.Updates.Busy && main.IsEnabled);
                Assert.False(main.Updates.Ready); Assert.True(api.RememberMe); Assert.Same(session, api.Session);
                main.Close(); Assert.True(client.Disposed); Assert.False(updateTimer.IsEnabled); Assert.False(restartTimer.IsEnabled);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                client.CompleteApply(); updateTimer.Stop(); restartTimer.Stop();
                var model = ((TetrapodPage)main.FindName("Tetrapod")).Model; model.Planned = ""; model.IsCalculating = false;
                foreach (Window owned in main.OwnedWindows.Cast<Window>().ToArray()) owned.Close(); main.Close();
            }
        };
        main.ShowDialog(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static T UpdateField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void SetUpdateField<T>(MainWindow window, string name, T value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void ExpireUpdateDeadline(MainWindow window)
    {
        SetUpdateField(window, "lastActivity", DateTimeOffset.UtcNow.AddMinutes(-3)); SetUpdateField<DateTimeOffset?>(window, "restartAt", DateTimeOffset.UtcNow.AddSeconds(-1)); window.Updates.RestartPending = true;
    }
    private sealed class WorkflowUpdateClient : IBackgroundUpdateClient
    {
        private readonly TaskCompletionSource apply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInstalled => true;
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }
        public bool Disposed { get; private set; }
        public Task<string?> Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Checks++; return Task.FromResult<string?>(Checks == 2 ? "0.4.0" : null); }
        public Task Download(Action<int> progress, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Downloads++; progress(100); return Task.CompletedTask; }
        public Task Apply(CancellationToken ct) { Applies++; return apply.Task.WaitAsync(ct); }
        public void CompleteApply() => apply.TrySetResult();
        public void Dispose() => Disposed = true;
    }
}
