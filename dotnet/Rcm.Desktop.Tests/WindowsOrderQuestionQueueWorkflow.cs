using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed partial class WindowsWorkflowTests
{
    private static void RunOrderQuestionQueueWorkflow()
    {
        var server = new QuestionQueueServer { LoseAnswerResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        Task.Run(() => api.Login("biuro", "0000", default)).GetAwaiter().GetResult();
        var window = new MainWindow(api);
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => window.Model.CanOpenOrderQuestions);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("OrderQuestionsNav")).Visibility);
                ((ButtonBase)window.FindName("OrderQuestionsNav")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var page = (OrderQuestionQueuePage)window.FindName("OrderQuestionWorkspace");
                await Until(() => page.Model.Rows.Count == 1 && !page.Model.Busy);
                ((DataGrid)page.FindName("QuestionList")).SelectedIndex = 0;
                await Capture(window, "order-questions-queue");
                var answerFlow = DriveOrderResources(window, async editor =>
                {
                    await Until(() => editor.Model.SelectedQuestion?.Id == 42 && !editor.Model.Busy);
                    Assert.True(editor.Model.CanEdit);
                    editor.Model.Draft = "Synthetic answer 12 mm";
                    window.Close(); Assert.True(window.IsVisible); Assert.True(editor.IsVisible);
                    var dismiss = DismissNo("Niezapisane dane"); editor.Close(); await dismiss; Assert.True(editor.IsVisible);
                    ((ButtonBase)editor.FindName("SaveResource")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => editor.Model.Uncertain && !editor.Model.Busy);
                    Assert.Equal("Synthetic answer 12 mm", editor.Model.Draft);
                    ((ButtonBase)editor.FindName("SaveResource")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !editor.Model.Dirty && !editor.Model.Busy);
                    Assert.Single(server.Receipts); Assert.Equal(server.RequestIds[0], server.RequestIds[1]); editor.Close();
                });
                ((ButtonBase)page.FindName("OpenQuestion")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await answerFlow;
                await Until(() => page.Model.Rows.Single().AnswerText == "Synthetic answer 12 mm" && !page.Model.Busy);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        };
        window.ShowDialog();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
