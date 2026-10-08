using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class EditorTests
{
    [Fact]
    public async Task Lost_response_keeps_values_and_retries_identical_command()
    {
        var handler = new Responses();
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Lost response after commit"));
        var customer = new CustomerDto(Guid.NewGuid(), 1, new("Synthetic typed name"), true, DateTimeOffset.UtcNow);
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SaveCustomerResult(customer, []))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic typed name", IsSynthetic = true };
        Assert.False(await form.Save(api, CancellationToken.None));
        Assert.True(form.Dirty);
        Assert.True(form.IsUncertain);
        Assert.False(form.CanEdit);
        Assert.Equal("Synthetic typed name", form.DisplayName);
        Assert.True(await form.Save(api, CancellationToken.None));
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.False(form.Dirty);
    }

    [Fact]
    public async Task Reauthentication_of_uncertain_save_retains_the_pending_command()
    {
        var handler = new Responses();
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Lost response"));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic retained" };
        await form.Save(api, CancellationToken.None);
        await Assert.ThrowsAsync<ApiFailure>(() => form.Save(api, CancellationToken.None));
        Assert.True(form.IsUncertain);
        Assert.False(form.CanEdit);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(409)]
    public async Task Rejected_save_preserves_input(int status)
    {
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(status, "Test error", new() { ["email"] = ["Invalid address"] }), (HttpStatusCode)status)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic retained", Email = "invalid" };
        if (status == 401) await Assert.ThrowsAsync<ApiFailure>(() => form.Save(api, CancellationToken.None));
        else Assert.False(await form.Save(api, CancellationToken.None));
        Assert.Equal("Synthetic retained", form.DisplayName);
        Assert.Equal("invalid", form.Email);
        Assert.True(form.Dirty);
        Assert.True(form.HasErrors);
        Assert.False(form.HasConflict);
    }

    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 409)]
    [InlineData(false, 410)]
    [InlineData(true, 410)]
    public async Task Archived_customer_rejects_topic_save_without_comparison_or_lost_draft(bool hadTopics, int status)
    {
        var customerId = Guid.NewGuid();
        var existing = hadTopics ? new TopicDto(Guid.NewGuid(), customerId, 1, new(["CT1"], "Existing")) : null;
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(status, "Klient został usunięty. Przywróć go przed zapisaniem zmian."), (HttpStatusCode)status)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Topic, customerId, topic: existing) { Need = "Synthetic draft after archive" };
        form.Products[0].Selected = true;

        Assert.False(await form.Save(api, CancellationToken.None));
        await form.LoadComparison(api, CancellationToken.None);

        Assert.False(form.HasConflict);
        Assert.True(form.IsLifecycleRejected);
        Assert.True(form.Dirty);
        Assert.Equal("Synthetic draft after archive", form.Need);
        Assert.Contains("Przywróć klienta", form.Status);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Nonversion_conflict_retries_with_current_topic_values()
    {
        var customerId = Guid.NewGuid();
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(410, "Klient został usunięty. Przywróć go przed zapisaniem zmian."), HttpStatusCode.Gone)));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new TopicDto(Guid.NewGuid(), customerId, 1, new(["CT1"], "Revised draft")))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Topic, customerId) { Need = "Initial draft" };
        form.Products[0].Selected = true;

        Assert.False(await form.Save(api, CancellationToken.None));
        form.Need = "Revised draft";
        Assert.True(await form.Save(api, CancellationToken.None));
        var first = JsonSerializer.Deserialize<CreateTopic>(handler.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var second = JsonSerializer.Deserialize<CreateTopic>(handler.Bodies[1], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Revised draft", second.Fields.Need);
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    [Fact]
    public async Task Missing_topic_during_comparison_keeps_form_and_disables_version_acceptance()
    {
        var customer = new CustomerDto(Guid.NewGuid(), 1, new("Synthetic"), true, DateTimeOffset.UtcNow);
        var topic = new TopicDto(Guid.NewGuid(), customer.Id, 1, new(["CT1"], "Original"));
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(409, "Dane zostały zmienione. Porównaj zapisane dane ze swoim formularzem."), HttpStatusCode.Conflict)));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new CustomerDetail(customer, []))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Topic, customer.Id, topic: topic) { Need = "My retained change" };

        Assert.False(await form.Save(api, CancellationToken.None));
        Assert.True(form.HasConflict);
        await form.LoadComparison(api, CancellationToken.None);
        form.AcceptComparedVersion();

        Assert.True(form.HasConflict);
        Assert.Equal(1, form.ExpectedVersion);
        Assert.Equal("My retained change", form.Need);
        Assert.Contains("nie jest już dostępny", form.Status);
    }

    [Fact]
    public async Task Conflict_comparison_does_not_replace_typed_values_and_requires_acceptance()
    {
        var customer = new CustomerDto(Guid.NewGuid(), 1, new("Original synthetic"), true, DateTimeOffset.UtcNow);
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new ApiError(409, "Dane zostały zmienione. Porównaj zapisane dane ze swoim formularzem."), HttpStatusCode.Conflict)));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new CustomerDetail(customer with { Version = 2, Fields = new("Server synthetic") }, []))));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SaveCustomerResult(customer with { Version = 3 }, []))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new EditorViewModel(EditorKind.Customer, customer.Id, customer) { DisplayName = "My synthetic change" };
        Assert.False(await form.Save(api, CancellationToken.None));
        await form.LoadComparison(api, CancellationToken.None);
        Assert.Equal("My synthetic change", form.DisplayName);
        Assert.Equal(1, form.ExpectedVersion);
        Assert.Contains("Server synthetic", form.Comparison);
        Assert.False(await form.Save(api, CancellationToken.None));
        form.AcceptComparedVersion();
        Assert.Equal(2, form.ExpectedVersion);
        Assert.True(await form.Save(api, CancellationToken.None));
        var command = JsonSerializer.Deserialize<EditCustomer>(handler.Bodies.Last(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, command.ExpectedVersion);
        Assert.Equal("My synthetic change", command.Fields.DisplayName);
    }

    [Fact]
    public async Task Pending_save_disables_repeat_submission_and_cancellation_keeps_input()
    {
        var started = new TaskCompletionSource();
        var handler = new Responses();
        handler.Replies.Enqueue(async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Reply(new { }); });
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        using var cancellation = new CancellationTokenSource();
        var form = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic pending" };
        var first = form.Save(api, cancellation.Token);
        await started.Task;
        Assert.False(form.CanEdit);
        Assert.False(form.CanClose);
        Assert.False(await form.Save(api, CancellationToken.None));
        cancellation.Cancel();
        Assert.False(await first);
        Assert.Single(handler.Bodies);
        Assert.True(form.Dirty);
        Assert.True(form.IsUncertain);
    }

    [Fact]
    public async Task Login_does_not_switch_identity_while_a_form_belongs_to_another_user()
    {
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new { access_token = "synthetic-first" })));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SessionDto(101, "Synthetic A", Guid.NewGuid(), "Synthetic team A"))));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new { access_token = "synthetic-second" })));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SessionDto(102, "Synthetic B", Guid.NewGuid(), "Synthetic team B"))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("biuro", "0000", CancellationToken.None);
        var failure = await Assert.ThrowsAsync<ApiFailure>(() => api.Login("biuro", "0000", CancellationToken.None));
        Assert.Equal(403, failure.Status);
        Assert.Equal(101, api.Session!.UserId);
    }

    [Theory]
    [InlineData("crm", false)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    [InlineData("biuro", true)]
    [InlineData("technolog", true)]
    [InlineData("ceo", true)]
    [InlineData("produkcja", true)]
    public async Task Legacy_navigation_uses_confirmed_session_role(string? role, bool expected)
    {
        var handler = new Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new { access_token = "synthetic-session" })));
        handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SessionDto(101, "Synthetic", Guid.NewGuid(), "Synthetic team", role))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        using var model = new MainViewModel(api);
        Assert.False(model.CanOpenLegacy);
        await api.Login("biuro", "0000", CancellationToken.None);
        Assert.Equal(expected, model.CanOpenLegacy);
    }

    [Fact]
    public async Task Same_user_reauthentication_and_logout_refresh_navigation()
    {
        var handler = new Responses();
        var team = Guid.NewGuid();
        foreach (var role in new[] { "biuro", "crm" })
        {
            handler.Replies.Enqueue(_ => Task.FromResult(Reply(new { access_token = "synthetic-" + role })));
            handler.Replies.Enqueue(_ => Task.FromResult(Reply(new SessionDto(101, "Synthetic", team, "Synthetic team", role))));
        }
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        using var model = new MainViewModel(api);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await api.Login("biuro", "0000", CancellationToken.None);
        Assert.True(model.CanOpenLegacy);
        changed.Clear();
        await api.Login("crm", "0000", CancellationToken.None);
        Assert.Equal(101, api.Session!.UserId);
        Assert.False(model.CanOpenLegacy);
        Assert.Contains(nameof(MainViewModel.CanOpenLegacy), changed);
        changed.Clear();
        api.Logout();
        Assert.Null(api.Session);
        Assert.False(model.CanOpenLegacy);
        Assert.Contains(nameof(MainViewModel.CanOpenLegacy), changed);
    }

    [Fact]
    public void Older_session_without_role_hides_legacy_navigation()
    {
        var session = JsonSerializer.Deserialize<SessionDto>("""
            {"userId":101,"name":"Synthetic","teamId":"00000000-0000-0000-0000-000000000001","teamName":"Synthetic team"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(session);
        Assert.Null(session.Role);
    }
    internal static HttpResponseMessage Reply<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    internal sealed class Responses : HttpMessageHandler
    {
        public Queue<Func<CancellationToken, Task<HttpResponseMessage>>> Replies { get; } = new();
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await Replies.Dequeue()(ct);
        }
    }
}
