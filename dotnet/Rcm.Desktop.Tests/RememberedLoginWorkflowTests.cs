using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunRememberedLoginWorkflow()
    {
        var directory = Directory.CreateTempSubdirectory("rcm-remembered-window-");
        try
        {
            var startup = new StartupWindow();
            startup.Show();
            ((ButtonBase)startup.FindName("SkipUpdate")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(startup.Cancellation.IsCancellationRequested);
            Assert.False(startup.Lifetime.IsCancellationRequested);
            startup.Complete();
            var endpoint = new Uri("http://localhost/");
            var store = new SavedLoginStore(endpoint, directory.FullName);
            using var api = new CrmClient(endpoint, new SyntheticServer(), store);
            var window = new LoginWindow(api);
            Exception? failure = null;
            window.Loaded += (_, _) =>
            {
                try
                {
                    ((TextBox)window.FindName("Username")).Text = "synthetic.user";
                    ((PasswordBox)window.FindName("Password")).Password = "synthetic-password";
                    ((CheckBox)window.FindName("Remember")).IsChecked = true;
                    ((ButtonBase)window.FindName("Submit")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                catch (Exception error) { failure = error; window.Close(); }
            };
            var accepted = window.ShowDialog();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(accepted);
            Assert.True(api.RememberMe);
            Assert.Equal(new SavedLogin("synthetic-device-token", 101), new SavedLoginStore(endpoint, directory.FullName).Read());
            api.Logout();
            Assert.False(api.HasSavedLogin);
            Assert.True(store.Read()!.PendingRevocation);
        }
        finally { directory.Delete(recursive: true); }
    }
}
