using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class RememberedLoginTests
{
    private static readonly SessionDto User = new(101, "Synthetic remembered user", Guid.NewGuid(), "Synthetic team", "crm");

    [Fact]
    public async Task Remembered_production_account_restores_shift_from_refresh_without_storing_it_locally()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic-shift", refresh_token = "synthetic-device-token", id = 101, default_shift = "II" })));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User with { Role = "produkcja" })));
        using var api = Client(server, store);
        Assert.True(await api.RestoreLogin(default));
        Assert.Equal("II", api.Session!.DefaultShift);
        Assert.Equal(new SavedLogin("synthetic-device-token", 101), store.Saved);
    }

    [Fact]
    public async Task Restart_restores_same_account_without_password_or_PIN()
    {
        var store = new Store(); var login = new EditorTests.Responses();
        login.Replies.Enqueue(_ => Task.FromResult(LoginReply("first")));
        login.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        using (var api = Client(login, store)) await api.LoginWithPassword("synthetic", "fictional password", default, true);
        Assert.Equal(new SavedLogin("synthetic-device-token", 101), store.Saved);
        Assert.True(JsonDocument.Parse(login.Bodies[0]).RootElement.GetProperty("rememberMe").GetBoolean());

        var restart = new EditorTests.Responses();
        restart.Replies.Enqueue(_ => Task.FromResult(LoginReply("after-restart")));
        restart.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        using var nextVersion = Client(restart, store);
        Assert.True(await nextVersion.RestoreLogin(default));
        Assert.Equal(User, nextVersion.Session);
        Assert.True(nextVersion.RememberMe);
        Assert.Equal("synthetic-device-token", JsonDocument.Parse(restart.Bodies[0]).RootElement.GetProperty("refreshToken").GetString());
        Assert.DoesNotContain("password", string.Join("", restart.Bodies));
    }

    [Fact]
    public async Task Expired_access_renews_once_and_replays_the_exact_unsent_save()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("initial")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("renewed")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        var customer = new CustomerDto(Guid.NewGuid(), 1, new("Synthetic saved customer"), true, DateTimeOffset.UtcNow);
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SaveCustomerResult(customer, []))));
        using var api = Client(server, store);
        await api.RestoreLogin(default);
        var command = new CreateCustomer(Guid.NewGuid(), customer.Fields, true);
        await api.Save<SaveCustomerResult>(HttpMethod.Post, "customers", command, default);
        Assert.Equal(server.Bodies[2], server.Bodies[5]);
        Assert.True(api.RememberMe);
        Assert.Equal("synthetic-device-token", store.Saved!.RefreshToken);
    }

    [Fact]
    public async Task Offline_start_keeps_the_remembered_login_for_retry()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => throw new HttpRequestException("offline"));
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("back-online")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        using var api = Client(server, store);
        await Assert.ThrowsAsync<HttpRequestException>(() => api.RestoreLogin(default));
        Assert.True(api.HasSavedLogin);
        Assert.Null(api.Session);
        Assert.True(await api.RestoreLogin(default));
    }

    [Fact]
    public async Task Rejected_device_requires_password_again()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Revoked"), HttpStatusCode.Unauthorized)));
        using var api = Client(server, store);
        Assert.False(await api.RestoreLogin(default));
        Assert.Null(store.Saved);
        Assert.Null(api.Session);
    }

    [Fact]
    public async Task Offline_logout_is_never_restored_and_revocation_retries_on_next_start()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("before-logout")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        server.Replies.Enqueue(_ => throw new HttpRequestException("offline logout"));
        using (var api = Client(server, store))
        {
            await api.RestoreLogin(default); await api.LogoutAsync(default);
            Assert.Null(api.Session); Assert.False(api.HasSavedLogin);
        }
        Assert.True(store.Saved!.PendingRevocation);
        var restart = new EditorTests.Responses();
        restart.Replies.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var next = Client(restart, store);
        Assert.False(await next.RestoreLogin(default));
        Assert.Null(store.Saved); Assert.Null(next.Session);
        Assert.Single(restart.Bodies);
    }

    [Fact]
    public async Task Logout_during_refresh_cannot_resurrect_the_session_or_remove_pending_revocation()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        var started = new TaskCompletionSource(); var finish = new TaskCompletionSource();
        server.Replies.Enqueue(async _ => { started.SetResult(); await finish.Task; return LoginReply("late-response"); });
        using var api = Client(server, store);
        var restore = api.RestoreLogin(default);
        await started.Task; api.Logout(); finish.SetResult();
        Assert.False(await restore);
        Assert.Null(api.Session);
        Assert.True(store.Saved!.PendingRevocation);
        Assert.False(api.RememberMe);
    }

    [Fact]
    public async Task Refresh_cannot_replace_the_owner_of_an_open_form()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("wrong-owner")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User with { UserId = 102 })));
        using var api = Client(server, store);
        Assert.False(await api.RestoreLogin(default));
        Assert.Null(api.Session); Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Concurrent_expired_requests_share_one_renewal()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var bothRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0; var expired = 0;
        using var api = new CrmClient(new Uri("http://localhost/"), new CallbackServer(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/refresh"))
            {
                var number = Interlocked.Increment(ref refreshes);
                if (number > 1) await bothRejected.Task;
                return LoginReply(number == 1 ? "initial" : "renewed");
            }
            if (path.EndsWith("/session")) return EditorTests.Reply(User);
            if (request.Headers.Authorization?.Parameter == "initial")
            {
                if (Interlocked.Increment(ref expired) == 2) bothRejected.SetResult();
                await bothRejected.Task;
                return EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized);
            }
            return EditorTests.Reply(new Page<CustomerRow>([], 0, 1, 50));
        }), store);
        await api.RestoreLogin(default);
        await Task.WhenAll(api.Get<Page<CustomerRow>>("customers", default), api.Get<Page<CustomerRow>>("customers", default));
        Assert.Equal(2, refreshes);
        Assert.Equal(2, expired);
    }

    [Fact]
    public async Task Temporary_refresh_failure_preserves_session_and_typed_customer_values()
    {
        var store = new Store { Saved = new("synthetic-device-token", 101) };
        var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(LoginReply("initial")));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(503, "Identity unavailable"), HttpStatusCode.ServiceUnavailable)));
        using var api = Client(server, store);
        await api.RestoreLogin(default);
        var form = new EditorViewModel(EditorKind.Customer) { DisplayName = "Synthetic retained draft" };
        Assert.False(await form.Save(api, default));
        Assert.True(form.Dirty);
        Assert.Equal("Synthetic retained draft", form.DisplayName);
        Assert.Equal(User, api.Session);
        Assert.True(api.HasSavedLogin);
        Assert.Equal(4, server.Bodies.Count);
    }

    [Fact]
    public async Task Attachment_upload_renews_session_and_reopens_identical_file_with_same_request_id()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("rcm-remembered-upload-");
        try
        {
            var source = System.IO.Path.Combine(folder.FullName, "synthetic.pdf");
            var bytes = "%PDF-1.7\nSynthetic remembered upload"u8.ToArray();
            await System.IO.File.WriteAllBytesAsync(source, bytes);
            var store = new Store { Saved = new("synthetic-device-token", 101) };
            var refreshes = 0; var uploads = 0; string? firstQuery = null;
            var item = new OrderAttachmentDto(42, 1, "synthetic.pdf", bytes.Length, "application/pdf", "technolog", DateTimeOffset.UtcNow);
            using var api = new CrmClient(new Uri("http://localhost/"), new CallbackServer(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/refresh")) return LoginReply(++refreshes == 1 ? "initial" : "renewed");
                if (path.EndsWith("/session")) return EditorTests.Reply(User);
                Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                if (++uploads == 1)
                {
                    firstQuery = request.RequestUri.Query;
                    return EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized);
                }
                Assert.Equal(firstQuery, request.RequestUri.Query);
                Assert.Equal("renewed", request.Headers.Authorization!.Parameter);
                return EditorTests.Reply(item);
            }), store);
            await api.RestoreLogin(default);
            Assert.Equal(item, await api.UploadOrderAttachment(1, source, Guid.NewGuid(), default));
            Assert.Equal(2, refreshes); Assert.Equal(2, uploads);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Unchecked_login_keeps_the_password_and_session_off_disk()
    {
        var store = new Store(); var server = new EditorTests.Responses();
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "temporary-session", id = 101 })));
        server.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(User)));
        using var api = Client(server, store);
        await api.LoginWithPassword("synthetic", "fictional password", default);
        Assert.Null(store.Saved); Assert.False(api.RememberMe);
    }

    private static CrmClient Client(HttpMessageHandler server, Store store) => new(new Uri("http://localhost/"), server, store);
    private static HttpResponseMessage LoginReply(string token) => EditorTests.Reply(new { access_token = token, refresh_token = "synthetic-device-token", id = 101 });
    private sealed class Store : ISavedLoginStore
    {
        public SavedLogin? Saved { get; set; }
        public SavedLogin? Read() => Saved;
        public void Write(SavedLogin login) => Saved = login;
        public void Clear() => Saved = null;
    }
    private sealed class CallbackServer(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request);
    }
}
