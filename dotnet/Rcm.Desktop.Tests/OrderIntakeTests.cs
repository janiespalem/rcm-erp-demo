using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderIntakeTests
{
    [Fact]
    public async Task Lost_intake_response_retries_original_receipt_and_preserves_form()
    {
        var server = new NativeOrdersServer { LoseCreateResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        var model = new OrderEditorViewModel(intake: true) { Client = "Synthetic customer", Description = "Synthetic repair", Quantity = "3" };
        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.True(model.Uncertain); Assert.False(model.CanEdit); Assert.Null(model.Saved);
        Assert.Equal("Synthetic repair", model.Description);
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Equal(2, server.IntakeRequests); Assert.Single(server.IntakeReceipts);
        Assert.Equal(server.RequestIds[0], server.RequestIds[1]);
        Assert.Equal("niestandard", model.Saved!.Status); Assert.Equal(3, model.Saved.Quantity);
        Assert.Contains("Synthetic warning", model.Status);
    }

    [Fact]
    public async Task Reauthentication_retains_intake_request_and_internal_order()
    {
        var server = new NativeOrdersServer { RejectCreateOnce = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        var model = new OrderEditorViewModel(intake: true) { Description = "Synthetic internal order" };
        model.SelectInternalFirm("Demo Concrete");
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, CancellationToken.None))).Status);
        Assert.True(model.InternalOrder); Assert.Equal("Demo Concrete", model.Client); Assert.False(model.CanEdit);
        await api.Login("biuro", "0000", CancellationToken.None);
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Equal(server.RequestIds[0], server.RequestIds[1]);
        Assert.Equal("in_production", model.Saved!.Status);
    }

    [Fact]
    public async Task Pending_intake_blocks_repeated_submission()
    {
        var handler = new PendingIntake();
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderEditorViewModel(intake: true) { Client = "Synthetic", Description = "Repair" };
        var first = model.Save(api, CancellationToken.None);
        Assert.True(model.Saving); Assert.False(await model.Save(api, CancellationToken.None));
        handler.Response.SetResult(EditorTests.Reply(new OrderIntakeResult(new() { Id = 1, VersionId = 2, Status = "niestandard", TriageBranch = "niestandard" }, new("niestandard", "Do wyceny", null, null, []))));
        Assert.True(await first); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Unconfirmed_routing_response_keeps_frozen_command_for_retry()
    {
        var handler = new PendingIntake();
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderEditorViewModel(intake: true) { Client = "Synthetic", Description = "Repair" };
        handler.Response.SetResult(EditorTests.Reply(new OrderIntakeResult(new() { Id = 1, VersionId = 1, Status = "draft" }, new("niestandard", "Do wyceny", null, null, []))));
        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.True(model.Uncertain); Assert.Null(model.Saved); Assert.Equal("Repair", model.Description);
    }

    [Fact]
    public async Task Missing_intake_after_server_rollback_does_not_release_uncertain_command()
    {
        var handler = new PendingIntake();
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderEditorViewModel(intake: true) { Client = "Synthetic", Description = "Repair" };
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { message = "Intake unavailable" }) });
        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.True(model.Uncertain); Assert.False(model.CanEdit); Assert.Null(model.Saved);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Failed_retry_after_lost_response_cannot_replace_request_even_after_comparison(HttpStatusCode failure)
    {
        var handler = new ConflictAfterLoss(failure);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderEditorViewModel(intake: true) { Client = "Synthetic", Description = "Repair" };
        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.True(model.Uncertain); Assert.False(model.CanEdit);
        await model.Compare(api, CancellationToken.None);
        Assert.True(model.Uncertain); Assert.False(model.CanEdit);
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count); Assert.Single(handler.Requests.Distinct());
    }

    [Fact]
    public async Task Compatibility_creation_still_saves_draft_without_intake()
    {
        var server = new NativeOrdersServer();
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        var model = new OrderEditorViewModel { Client = "Synthetic", Description = "Repair" };
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Equal("draft", model.Saved!.Status); Assert.Equal(0, server.IntakeRequests);
    }

    private sealed class PendingIntake : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/v1/orders/intake", request.RequestUri!.AbsolutePath);
            Requests++;
            return await Response.Task.WaitAsync(ct);
        }
    }

    private sealed class ConflictAfterLoss(HttpStatusCode failure) : HttpMessageHandler
    {
        public List<Guid> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((await request.Content!.ReadFromJsonAsync<CreateOrder>(ct))!.RequestId);
            if (Requests.Count == 1) throw new HttpRequestException("Synthetic lost committed response");
            if (Requests.Count == 2) return new(failure) { Content = JsonContent.Create(new { message = "Retry denied" }) };
            return EditorTests.Reply(new OrderIntakeResult(new() { Id = 1, VersionId = 2, Status = "niestandard", TriageBranch = "niestandard" }, new("niestandard", "Do wyceny", null, null, [])));
        }
    }
}
