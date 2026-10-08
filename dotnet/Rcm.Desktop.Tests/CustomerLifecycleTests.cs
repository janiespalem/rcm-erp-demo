using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class CustomerLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_response_retries_identical_target_command(bool restore)
    {
        var customer = Customer(restore);
        var handler = new Requests();
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Lost response after commit"));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer with { Version = 5, ArchivedAt = restore ? null : DateTimeOffset.UtcNow })));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new CustomerLifecycleViewModel(customer, restore);

        Assert.False(await model.Save(api, CancellationToken.None));
        Assert.True(model.IsUncertain);
        Assert.Null(model.Saved);
        Assert.Equal(4, model.ExpectedVersion);
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.All(handler.Paths, path => Assert.Equal($"/api/v1/crm/customers/{customer.Id}/{(restore ? "restore" : "archive")}", path));
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Post, method));
        var command = JsonSerializer.Deserialize<ChangeCustomerLifecycle>(handler.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(4, command.ExpectedVersion);
        Assert.NotEqual(Guid.Empty, command.RequestId);
        Assert.Equal(5, model.Saved!.Version);
        Assert.False(model.IsUncertain);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(410)]
    [InlineData(503)]
    public async Task Failure_preserves_customer_and_never_claims_success(int status)
    {
        var handler = new Requests();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(status, "Rejected"), (HttpStatusCode)status)));
        var customer = Customer();
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new CustomerLifecycleViewModel(customer, false);
        if (status == 401) await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, CancellationToken.None));
        else Assert.False(await model.Save(api, CancellationToken.None));
        Assert.Null(model.Saved);
        Assert.Equal(customer, model.Customer);
        Assert.Equal(4, model.ExpectedVersion);
        Assert.Equal(status == 409, model.HasConflict);
        Assert.Equal(status >= 500, model.IsUncertain);
        Assert.False(model.IsSaving);
        Assert.NotEmpty(model.Status);
    }

    [Fact]
    public async Task Uncertain_retry_and_reauthentication_keep_request_identity()
    {
        var handler = new Requests();
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Lost"));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new CustomerLifecycleViewModel(Customer(), false);
        await model.Save(api, CancellationToken.None);
        await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, CancellationToken.None));
        Assert.True(model.IsUncertain);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task Conflict_requires_comparison_and_explicit_version_acceptance()
    {
        var customer = Customer();
        var handler = new Requests();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(409, "Changed"), HttpStatusCode.Conflict)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer with { Version = 8, Fields = new("Latest name") })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer with { Version = 9, ArchivedAt = DateTimeOffset.UtcNow })));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new CustomerLifecycleViewModel(customer, false);

        Assert.False(await model.Save(api, CancellationToken.None));
        model.AcceptComparedVersion();
        Assert.True(model.HasConflict);
        Assert.False(await model.Save(api, CancellationToken.None));
        await model.ReloadComparison(api, CancellationToken.None);
        Assert.Contains("Latest name", model.Comparison);
        Assert.Equal(customer, model.Customer);
        Assert.Equal(4, model.ExpectedVersion);
        Assert.False(await model.Save(api, CancellationToken.None));
        model.AcceptComparedVersion();
        Assert.Equal(8, model.ExpectedVersion);
        Assert.True(await model.Save(api, CancellationToken.None));
        var initial = JsonSerializer.Deserialize<ChangeCustomerLifecycle>(handler.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var retry = JsonSerializer.Deserialize<ChangeCustomerLifecycle>(handler.Bodies[2], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotEqual(initial.RequestId, retry.RequestId);
        Assert.Equal(8, retry.ExpectedVersion);
        Assert.EndsWith("/record", handler.Paths[1]);
    }

    [Fact]
    public async Task Pending_save_blocks_duplicate_and_cancellation_remains_uncertain()
    {
        var handler = new Requests();
        handler.Replies.Enqueue(async ct => { await Task.Delay(Timeout.Infinite, ct); return EditorTests.Reply(new { }); });
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        using var cancellation = new CancellationTokenSource();
        var model = new CustomerLifecycleViewModel(Customer(), false);
        var pending = model.Save(api, cancellation.Token);
        Assert.True(model.IsSaving);
        Assert.False(model.CanClose);
        Assert.False(await model.Save(api, CancellationToken.None));
        cancellation.Cancel();
        Assert.False(await pending);
        Assert.True(model.IsUncertain);
        Assert.Single(handler.Bodies);
    }

    private static CustomerDto Customer(bool archived = false) => new(Guid.NewGuid(), 4, new("Synthetic"), true, DateTimeOffset.UtcNow,
        archived ? DateTimeOffset.UtcNow : null, archived ? 101 : null);

    internal sealed class Requests : HttpMessageHandler
    {
        public Queue<Func<CancellationToken, Task<HttpResponseMessage>>> Replies { get; } = new();
        public List<string> Bodies { get; } = [];
        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            Paths.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method);
            return await Replies.Dequeue()(ct);
        }
    }
}
