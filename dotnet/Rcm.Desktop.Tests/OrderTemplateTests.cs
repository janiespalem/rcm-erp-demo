using System.Net;
using System.Net.Http;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderTemplateTests
{
    [Fact]
    public async Task Lost_template_response_preserves_draft_and_retries_same_request_id()
    {
        var server = new NativeOrdersServer { Role = "technolog", LoseTemplateResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new OrderTemplateViewModel(server.Order) { Name = "Synthetic reusable repair", Category = "remont" };
        Assert.False(await model.Save(api, default));
        Assert.True(model.Uncertain); Assert.True(model.Dirty); Assert.False(model.CanEdit);
        Assert.Equal("Synthetic reusable repair", model.Name);
        Assert.True(await model.Save(api, default));
        Assert.Single(server.TemplateReceipts); Assert.Equal(server.TemplateRequestIds[0], server.TemplateRequestIds[1]);
        Assert.Equal("Synthetic reusable repair", model.Saved!.Name); Assert.False(model.Dirty);
    }

    [Fact]
    public async Task Template_reauthentication_preserves_command_and_input()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "technolog"))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic-again" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "technolog"))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new OrderSavedTemplate(42, "Synthetic retained", "remont"))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("technolog", "0000", default);
        var model = new OrderTemplateViewModel(new() { Id = 1 }) { Name = "Synthetic retained" };
        await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, default));
        Assert.True(model.Uncertain); Assert.Equal("Synthetic retained", model.Name);
        await api.Login("technolog", "0000", default);
        Assert.True(await model.Save(api, default)); Assert.Equal(handler.Bodies[2], handler.Bodies[5]);
    }

    [Fact]
    public async Task Validation_error_stays_in_form_and_can_be_corrected()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "technolog"))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(422, "Invalid category", new() { ["category"] = ["Synthetic field error"] }), HttpStatusCode.UnprocessableEntity)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("technolog", "0000", default);
        var model = new OrderTemplateViewModel(new() { Id = 1 }) { Name = "Synthetic retained", Category = "special" };
        Assert.False(await model.Save(api, default));
        Assert.Equal("Synthetic retained", model.Name); Assert.Equal("special", model.Category);
        Assert.Equal("Synthetic field error", model.CategoryError); Assert.True(model.CanEdit); Assert.True(model.Dirty);
    }

    [Theory]
    [InlineData("biuro", false)]
    [InlineData("ceo", false)]
    [InlineData("technolog", true)]
    public async Task Unauthorized_or_archived_order_sends_no_template_command(string role, bool archived)
    {
        var server = new NativeOrdersServer { Role = role };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login(role, "0000", default);
        var model = new OrderTemplateViewModel(server.Order with { ArchivedAt = archived ? DateTimeOffset.UtcNow : null });
        Assert.False(await model.Save(api, default)); Assert.Empty(server.TemplateRequestIds);
    }

    [Fact]
    public async Task Logout_keeps_draft_and_prevents_saving()
    {
        var server = new NativeOrdersServer { Role = "technolog" };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new OrderTemplateViewModel(server.Order) { Name = "Synthetic draft", Role = "technolog" };
        api.Logout();
        Assert.False(await model.Save(api, default)); Assert.Equal("Synthetic draft", model.Name);
        Assert.True(model.Dirty); Assert.False(model.CanEdit); Assert.Empty(server.TemplateRequestIds);
    }
}
