using System.Net;
using System.Net.Http;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Summary_previews_are_partial_and_archived_list_clears_on_logout()
    {
        var handler = new CustomerLifecycleTests.Requests();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", Guid.NewGuid(), "Team", "crm"))));
        var customer = Customer("Archived") with { ArchivedAt = DateTimeOffset.UtcNow };
        var previews = Enumerable.Range(0, 3).Select(_ => new TopicSummary(Guid.NewGuid(), ["CT1"], "new", null)).ToArray();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<CustomerSummaryRow>([new(customer with { ArchivedAt = null }, previews, 101, null)], 1, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<CustomerDto>([customer], 1, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);

        await model.Load(CancellationToken.None);
        var row = Assert.Single(model.Rows);
        Assert.StartsWith("/api/v1/crm/customers/summaries?", handler.Paths.Last());
        Assert.Equal(101, row.TopicCount);
        Assert.Equal(3, row.TopicPreviews.Length);
        Assert.Empty(row.Topics);
        Assert.Contains("3 z 101", row.Preview);
        Assert.Contains("podgląd", row.Next.ToLowerInvariant());
        model.Archived = true;
        model.Section = MainSection.Archived;
        await model.Load(CancellationToken.None);
        Assert.StartsWith("/api/v1/crm/customers/archived?", handler.Paths.Last());
        Assert.Equal(customer, Assert.Single(model.Rows).Customer);
        Assert.Null(model.Detail);
        Assert.True(model.IsArchivedActive);
        api.Logout();
        Assert.False(model.Archived);
        Assert.False(model.Queue);
        Assert.Empty(model.Rows);
        Assert.Equal(0, model.TopicTotal);
        Assert.False(model.CanAddTopic);
    }

    [Fact]
    public async Task Archived_detail_and_topic_pages_preserve_plans_and_history_readonly()
    {
        var handler = new CustomerLifecycleTests.Requests();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", Guid.NewGuid(), "Team", "crm"))));
        var customer = Customer("Archived") with { ArchivedAt = DateTimeOffset.UtcNow };
        var first = Topic(customer) with { Fields = new(["CT1"], "Need", NextContact: new(null, "Next month", "Call")) };
        var next = Topic(customer);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<TopicDto>([first], 75, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<TopicDto>([next], 75, 2, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);

        await model.Open(customer.Id, null, CancellationToken.None);
        Assert.False(model.CanEditCustomer);
        Assert.False(model.CanAddTopic);
        Assert.False(model.CanActOnTopic);
        Assert.Contains("Next month", model.TopicSummary);
        Assert.True(model.HistoryLoaded);
        Assert.Equal(75, model.TopicTotal);
        await model.LoadTopicPage(2, CancellationToken.None);
        Assert.Equal(2, model.TopicPage);
        Assert.Equal(next.Id, model.SelectedTopic!.Id);
        Assert.Equal(next.Id, Assert.Single(model.Detail!.Topics).Id);
        Assert.Contains($"/api/v1/crm/customers/{customer.Id}/topics?page=2&pageSize=50", handler.Paths);
        Assert.DoesNotContain($"/api/v1/crm/customers/{customer.Id}", handler.Paths);
    }

    [Fact]
    public async Task Superseded_search_without_cancellation_cannot_publish_old_rows()
    {
        var handler = new EditorTests.Responses();
        Login(handler);
        var oldReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Replies.Enqueue(_ => oldReply.Task);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<CustomerSummaryRow>([new(Customer("Fresh"), [], 0, null)], 1, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);
        var old = model.Load(CancellationToken.None);
        await model.Load(CancellationToken.None);
        oldReply.SetResult(EditorTests.Reply(new Page<CustomerSummaryRow>([new(Customer("Old"), [], 0, null)], 1, 1, 50)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal("Fresh", Assert.Single(model.Rows).Customer.Fields.DisplayName);
    }

    [Fact]
    public async Task Cancelled_topic_page_cannot_replace_new_customer()
    {
        var handler = new EditorTests.Responses();
        Login(handler);
        var first = Customer("First");
        var second = Customer("Second");
        OpenReplies(handler, first, []);
        var oldReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Replies.Enqueue(_ => oldReply.Task);
        OpenReplies(handler, second, []);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);
        await model.Open(first.Id, null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var old = model.LoadTopicPage(2, cancellation.Token);
        cancellation.Cancel();
        await model.Open(second.Id, null, CancellationToken.None);
        oldReply.SetResult(EditorTests.Reply(new Page<TopicDto>([], 75, 2, 50)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(second.Id, model.Detail!.Customer.Id);
        Assert.Equal(1, model.TopicPage);
        Assert.Equal(0, model.TopicTotal);
        Assert.False(model.HistoryBusy);
    }

    [Fact]
    public async Task Failed_history_does_not_replace_open_customer()
    {
        var handler = new EditorTests.Responses();
        var first = Customer("First synthetic");
        var second = Customer("Second synthetic");
        var firstTopic = Topic(first);
        var secondTopic = Topic(second);
        Login(handler);
        OpenReplies(handler, first, [firstTopic]);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        OpenReplies(handler, second, [secondTopic]);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(503, "Synthetic outage"), HttpStatusCode.ServiceUnavailable)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);

        await model.Open(first.Id, firstTopic.Id, CancellationToken.None);
        await Assert.ThrowsAsync<ApiFailure>(() => model.Open(second.Id, secondTopic.Id, CancellationToken.None));

        Assert.Equal(first.Id, model.Detail!.Customer.Id);
        Assert.Equal(firstTopic.Id, model.SelectedTopic!.Id);
        Assert.Equal("First synthetic", model.PageTitle);
        Assert.True(model.HistoryLoaded);
        Assert.False(model.HistoryFailed);
    }

    [Fact]
    public async Task Failed_topic_history_keeps_previous_topic_and_exposes_retry_state()
    {
        var handler = new EditorTests.Responses();
        var customer = Customer("Synthetic customer");
        var first = Topic(customer);
        var second = Topic(customer);
        Login(handler);
        OpenReplies(handler, customer, [first, second]);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(503, "Synthetic outage"), HttpStatusCode.ServiceUnavailable)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);

        await model.Open(customer.Id, first.Id, CancellationToken.None);
        await Assert.ThrowsAsync<ApiFailure>(() => model.SelectTopic(second, CancellationToken.None));
        Assert.Equal(first.Id, model.SelectedTopic!.Id);
        Assert.True(model.HistoryFailed);
        Assert.True(model.CanActOnTopic);
        await model.SelectTopic(second, CancellationToken.None);
        Assert.Equal(second.Id, model.SelectedTopic!.Id);
        Assert.False(model.HistoryFailed);
    }

    [Fact]
    public async Task Cancelled_old_search_cannot_replace_new_page_or_clear_its_loading_state()
    {
        var handler = new EditorTests.Responses();
        var oldReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Login(handler);
        handler.Replies.Enqueue(_ => oldReply.Task);
        handler.Replies.Enqueue(_ => freshReply.Task);
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);
        using var oldCancellation = new CancellationTokenSource();

        var old = model.Load(oldCancellation.Token);
        oldCancellation.Cancel();
        model.Search = "fresh";
        var fresh = model.Load(CancellationToken.None);
        oldReply.SetResult(EditorTests.Reply(new Page<CustomerSummaryRow>([new(Customer("Old synthetic"), [], 0, null)], 1, 1, 50)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.True(model.Busy);
        freshReply.SetResult(EditorTests.Reply(new Page<CustomerSummaryRow>([new(Customer("Fresh synthetic"), [], 0, null)], 1, 1, 50)));
        await fresh;

        Assert.False(model.Busy);
        Assert.Equal("Fresh synthetic", Assert.Single(model.Rows).Customer.Fields.DisplayName);
    }

    private static void OpenReplies(EditorTests.Responses handler, CustomerDto customer, TopicDto[] topics)
    {
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<TopicDto>(topics, topics.Length, 1, 50))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_topic_history_cannot_replace_new_selection(bool cancelOld)
    {
        var handler = new EditorTests.Responses();
        Login(handler);
        var customer = Customer("Synthetic");
        var first = Topic(customer);
        var oldTopic = Topic(customer);
        var freshTopic = Topic(customer);
        OpenReplies(handler, customer, [first, oldTopic, freshTopic]);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        var oldReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Replies.Enqueue(_ => oldReply.Task);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 3, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);
        await model.Open(customer.Id, null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var old = model.SelectTopic(oldTopic, cancellation.Token);
        if (cancelOld) cancellation.Cancel();
        await model.SelectTopic(freshTopic, CancellationToken.None);
        oldReply.SetResult(EditorTests.Reply(new Page<ContactEventDto>([], 99, 1, 50)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(freshTopic.Id, model.SelectedTopic!.Id);
        Assert.Equal(3, model.HistoryTotal);
        Assert.False(model.HistoryBusy);
        Assert.False(model.HistoryFailed);
    }

    [Fact]
    public async Task Topic_page_history_failure_keeps_page_selection_and_history_together()
    {
        var handler = new EditorTests.Responses();
        Login(handler);
        var customer = Customer("Synthetic");
        var first = Topic(customer);
        var second = Topic(customer);
        OpenReplies(handler, customer, [first]);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 8, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<TopicDto>([second], 51, 2, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(503, "Outage"), HttpStatusCode.ServiceUnavailable)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);
        await model.Open(customer.Id, null, CancellationToken.None);

        await Assert.ThrowsAsync<ApiFailure>(() => model.LoadTopicPage(2, CancellationToken.None));
        Assert.Equal(1, model.TopicPage);
        Assert.Equal(first.Id, Assert.Single(model.Topics).Topic.Id);
        Assert.Equal(first.Id, model.SelectedTopic!.Id);
        Assert.Equal(8, model.HistoryTotal);
        Assert.False(model.HistoryBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_topic_outside_first_page_uses_direct_route_and_validates_customer(bool foreign)
    {
        var handler = new CustomerLifecycleTests.Requests();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", Guid.NewGuid(), "Team", "crm"))));
        var customer = Customer("Synthetic");
        var first = Topic(customer);
        var selected = Topic(foreign ? Customer("Other") : customer);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(customer)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<TopicDto>([first], 1000, 1, 50))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(selected)));
        if (!foreign) handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<ContactEventDto>([], 0, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        await api.Login("crm", "0000", CancellationToken.None);
        using var model = new MainViewModel(api);

        if (foreign)
        {
            await Assert.ThrowsAsync<ApiFailure>(() => model.Open(customer.Id, selected.Id, CancellationToken.None));
            Assert.Null(model.Detail);
        }
        else
        {
            await model.Open(customer.Id, selected.Id, CancellationToken.None);
            Assert.Equal(selected.Id, model.SelectedTopic!.Id);
            Assert.Contains("spoza strony", model.TopicPageInfo);
            Assert.Equal(first.Id, Assert.Single(model.Detail!.Topics).Id);
        }
        Assert.Contains($"/api/v1/crm/topics/{selected.Id}", handler.Paths);
        Assert.Single(handler.Paths, path => path.Contains("/topics?page="));
    }

    private static void Login(EditorTests.Responses handler)
    {
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", Guid.NewGuid(), "Team", "crm"))));
    }

    private static CustomerDto Customer(string name) => new(Guid.NewGuid(), 1, new(name), true, DateTimeOffset.UtcNow);
    private static TopicDto Topic(CustomerDto customer) => new(Guid.NewGuid(), customer.Id, 1, new(["CT1"], "Synthetic need"));
}
