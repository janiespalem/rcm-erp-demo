using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderQuestionQueueTests
{
    [Fact]
    public async Task Queue_search_retains_latest_result_when_old_transport_ignores_cancellation()
    {
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => delayed.Task);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<OrderQuestionQueueRow>([QuestionQueueServer.Question(2, "New question")], 1, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderQuestionQueueViewModel(api) { Available = true, Search = "Old" };
        var old = model.Load(default); model.Search = "New"; await model.Load(default);
        delayed.SetResult(EditorTests.Reply(new Page<OrderQuestionQueueRow>([QuestionQueueServer.Question(1, "Old question")], 1, 1, 50)));
        await old;
        Assert.Equal("New question", Assert.Single(model.Rows).QuestionText);
    }

    [Theory]
    [InlineData("biuro", true)]
    [InlineData("technolog", true)]
    [InlineData("ceo", false)]
    public async Task Queue_menu_requires_native_resources_and_existing_office_or_technology_role(string role, bool visible)
    {
        var server = new QuestionQueueServer { Role = role };
        using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(role, "0000", default);
        using var main = new MainViewModel(api);
        Assert.False(main.CanOpenOrderQuestions);
        main.SupportsOrderResources = true;
        Assert.Equal(visible, main.CanOpenOrderQuestions);
        main.Section = MainSection.OrderQuestions;
        Assert.Equal(visible, main.IsOrderQuestionsVisible);
        main.SupportsOrderResources = false; Assert.False(main.IsOrderQuestionsVisible);
    }

    [Fact]
    public async Task Queue_opens_correct_order_and_existing_answer_editor_retries_same_identity_after_lost_response()
    {
        var server = new QuestionQueueServer { LoseAnswerResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("biuro", "0000", default);
        var queue = new OrderQuestionQueueViewModel(api) { Available = true }; await queue.Load(default);
        queue.Selected = Assert.Single(queue.Rows);
        var order = await queue.OpenSelected(default); Assert.Equal(7, order!.Id);
        var editor = new OrderResourcesViewModel(order); await editor.Load(api, default);
        Assert.True(editor.SelectQuestion(Assert.Single(editor.Questions))); editor.Draft = "Synthetic answer 12 mm";
        Assert.False(await editor.Save(api, default)); Assert.True(editor.Uncertain); Assert.Equal("Synthetic answer 12 mm", editor.Draft);
        Assert.True(await editor.Save(api, default)); Assert.Equal(server.RequestIds[0], server.RequestIds[1]); Assert.Single(server.Receipts);
        await queue.Load(default); Assert.Equal("Synthetic answer 12 mm", Assert.Single(queue.Rows).AnswerText);
    }
}

internal sealed class QuestionQueueServer : HttpMessageHandler
{
    internal string Role { get; set; } = "biuro";
    internal bool LoseAnswerResponse { get; set; }
    internal OrderDto Order { get; } = new() { Id = 7, VersionId = 1, OrderNumber = "7/2026", Client = "Synthetic queue customer", Deadline = new(2026, 10, 20) };
    private OrderQuestionDto question = new(42, 7, "Synthetic thickness?", null, "pending", DateTimeOffset.UtcNow, null);
    internal Dictionary<Guid, OrderQuestionDto> Receipts { get; } = [];
    internal List<Guid> RequestIds { get; } = [];
    internal static OrderQuestionQueueRow Question(long id, string text) => new(id, 7, "7/2026", "Synthetic queue customer", new(2026, 10, 20), null, text, null, "pending", DateTimeOffset.UtcNow, null);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/shift-reports/features") return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures);
        if (path == "/api/v1/session/login") return EditorTests.Reply(new { access_token = "synthetic-queue-token" });
        if (path == "/api/v1/session") return EditorTests.Reply(new SessionDto(101, "Synthetic queue operator", null, null, Role));
        if (path == "/api/v1/orders/features") return EditorTests.Reply(new OrderFeatures(true, true, true, true));
        if (path == "/api/v1/orders/questions") return EditorTests.Reply(new Page<OrderQuestionQueueRow>([
            Question(question.Id, question.QuestionText) with { AnswerText = question.AnswerText, Status = question.Status, AnsweredAt = question.AnsweredAt }], 1, 1, 50));
        if (path == "/api/v1/orders/7") return EditorTests.Reply(Order);
        if (path == "/api/v1/orders/7/operations") return EditorTests.Reply(Array.Empty<OrderOperationRow>());
        if (path == "/api/v1/orders/7/questions") return EditorTests.Reply(new[] { question });
        if (path == "/api/v1/orders/7/questions/42/answer")
        {
            var input = (await request.Content!.ReadFromJsonAsync<AnswerOrderQuestion>(ct))!;
            RequestIds.Add(input.RequestId);
            if (!Receipts.TryGetValue(input.RequestId, out var saved))
                Receipts.Add(input.RequestId, saved = question = question with { Status = "answered", AnswerText = input.AnswerText, AnsweredAt = DateTimeOffset.UtcNow });
            if (LoseAnswerResponse) { LoseAnswerResponse = false; throw new HttpRequestException("Synthetic lost answer reply"); }
            return EditorTests.Reply(saved);
        }
        throw new InvalidOperationException($"Unexpected queue request {request.Method} {path}");
    }
}
