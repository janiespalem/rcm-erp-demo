using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class TetrapodViewModelTests
{
    internal static TetrapodPlan Result() => new(new(10, 2, 8), new(40, 8, 32), new(1, 4, 88.125, 16, 72.125),
        [new("ringSmall", "Krąg mały", 6, 1220, 1, 0.271, null, new(1, 4, 40, 8, 32), new(0.271, 1.084, 10.84, 2.168, 8.672))],
        [new(6, new(40, 48.8, 10.84), new(8, 9.76, 2.168), new(32, 39.04, 8.672), 100.25, 2.168, 98.082, 89.41)]);

    [Theory]
    [InlineData("abc", "0", "")]
    [InlineData("", "0", "")]
    [InlineData("1,5", "0", "")]
    [InlineData("10", "11", "")]
    [InlineData("10", "0", "-1")]
    [InlineData("10", "0", "1,000.5")]
    [InlineData("NaN", "0", "")]
    public async Task Invalid_input_stays_visible_and_never_sends_a_request(string planned, string completed, string delivery)
    {
        var handler = new EditorTests.Responses();
        using var api = new CrmClient(new("http://localhost/"), handler);
        var model = new TetrapodViewModel { Planned = planned, Completed = completed, Delivery6 = delivery };
        Assert.False(await model.Calculate(api, default));
        Assert.Equal(planned, model.Planned);
        Assert.Equal(completed, model.Completed);
        Assert.Equal(delivery, model.Delivery6);
        Assert.Empty(handler.Bodies);
        Assert.NotEmpty(model.Status);
    }

    [Fact]
    public async Task Polish_decimal_input_uses_server_results_and_editing_hides_stale_results()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(Result())));
        using var api = new CrmClient(new("http://localhost/"), handler);
        var model = new TetrapodViewModel { Planned = "10", Completed = "2", Delivery6 = "100,25", Delivery12 = "0", Delivery16 = "" };
        Assert.True(await model.Calculate(api, default));
        var input = System.Text.Json.JsonSerializer.Deserialize<TetrapodInput>(handler.Bodies.Single(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(100.25, input.DeliveriesKg![6]);
        Assert.Equal(0, input.DeliveriesKg[12]);
        Assert.Null(input.DeliveriesKg[16]);
        Assert.Equal(88.125, model.Result!.WeightsKg.Planned);
        Assert.True(model.Dirty);
        model.Planned = "12";
        Assert.Null(model.Result);
        Assert.False(model.HasResult);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    public async Task Rejected_calculation_keeps_all_fields(int status)
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(status, "Synthetic rejection"), (HttpStatusCode)status)));
        using var api = new CrmClient(new("http://localhost/"), handler);
        var model = new TetrapodViewModel { Planned = "10", Completed = "2", Delivery6 = "15,25" };
        if (status == 401) await Assert.ThrowsAsync<ApiFailure>(() => model.Calculate(api, default));
        else Assert.False(await model.Calculate(api, default));
        Assert.Equal("10", model.Planned);
        Assert.Equal("2", model.Completed);
        Assert.Equal("15,25", model.Delivery6);
        Assert.True(model.Dirty);
        Assert.True(model.CanEdit);
        Assert.Null(model.Result);
    }

    [Fact]
    public async Task Network_failure_and_same_user_reauthentication_preserve_the_calculation()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "biuro"))));
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Offline"));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(401, "Expired"), HttpStatusCode.Unauthorized)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic-renewed" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "biuro"))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(Result())));
        using var api = new CrmClient(new("http://localhost/"), handler);
        await api.Login("biuro", "0000", default);
        var model = new TetrapodViewModel { Planned = "10", Completed = "2", Delivery16 = "200,50" };
        Assert.False(await model.Calculate(api, default));
        await Assert.ThrowsAsync<ApiFailure>(() => model.Calculate(api, default));
        await api.Login("biuro", "0000", default);
        Assert.True(await model.Calculate(api, default));
        Assert.Equal(handler.Bodies[2], handler.Bodies[3]);
        Assert.Equal(handler.Bodies[2], handler.Bodies[6]);
        Assert.Equal("200,50", model.Delivery16);
    }

    [Fact]
    public async Task Pending_calculation_is_cancellable_and_cannot_be_submitted_twice()
    {
        var started = new TaskCompletionSource();
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return EditorTests.Reply(Result()); });
        using var api = new CrmClient(new("http://localhost/"), handler);
        using var cancellation = new CancellationTokenSource();
        var model = new TetrapodViewModel { Planned = "10", Completed = "2" };
        var pending = model.Calculate(api, cancellation.Token);
        await started.Task;
        Assert.False(model.CanEdit);
        Assert.True(model.IsCalculating);
        Assert.False(await model.Calculate(api, default));
        cancellation.Cancel();
        Assert.False(await pending);
        Assert.Single(handler.Bodies);
        Assert.Equal("10", model.Planned);
        Assert.True(model.CanEdit);
        Assert.True(model.Dirty);
    }

    [Fact]
    public async Task Unreadable_server_response_preserves_input_for_retry()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{broken", System.Text.Encoding.UTF8, "application/json") }));
        using var api = new CrmClient(new("http://localhost/"), handler);
        var model = new TetrapodViewModel { Planned = "10", Completed = "2" };
        Assert.False(await model.Calculate(api, default));
        Assert.Equal("10", model.Planned);
        Assert.True(model.CanEdit);
        Assert.Null(model.Result);
        Assert.NotEmpty(model.Status);
    }

    [Theory]
    [InlineData("biuro", true)]
    [InlineData("technolog", true)]
    [InlineData("ceo", false)]
    [InlineData("produkcja", false)]
    [InlineData("crm", false)]
    [InlineData(null, false)]
    public async Task Unassigned_shell_does_not_request_CRM_and_calculator_uses_confirmed_role(string? role, bool calculate)
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" })));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, role))));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<CustomerRow>([], 0, 1, 50))));
        using var api = new CrmClient(new("http://localhost/"), handler);
        await api.Login("biuro", "0000", default);
        using var model = new MainViewModel(api);
        Assert.False(model.HasCrmAccess);
        Assert.Equal(calculate, model.CanCalculate);
        await model.Load(default);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Empty(model.Rows);
        Assert.Equal(calculate ? "Tetrapod" : "Moduły", model.PageTitle);
        Assert.Equal(calculate ? MainSection.Tetrapod : MainSection.Modules, model.Section);
    }
    [Fact]
    public async Task History_totals_feed_existing_calculator_and_manual_values_remain_available()
    {
        var store = new MemoryTetrapodDeliveryStore(); var model = new TetrapodViewModel(101, store) { DeliveryAccess = true, Planned = "10", Completed = "2", Delivery6 = "999" };
        Assert.True(await model.LoadDeliveries(default));
        model.NewDelivery6 = "100,25"; model.NewDelivery12 = "10";
        Assert.True(model.AddDelivery(DateTimeOffset.Parse("2026-10-01T09:30:00Z")));
        Assert.True(model.HistoryDirty); Assert.True(model.Dirty); Assert.Equal("11:30", model.Deliveries[0].Time); Assert.Equal("czwartek", model.Deliveries[0].Weekday);
        model.NewDelivery6 = "1,5"; model.NewDelivery16 = "20"; Assert.True(model.AddDelivery());
        var handler = new EditorTests.Responses(); handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(Result()))); handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(Result())));
        using var api = new CrmClient(new("http://localhost/"), handler);
        Assert.True(await model.Calculate(api, default));
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var input = System.Text.Json.JsonSerializer.Deserialize<TetrapodInput>(handler.Bodies[0], json)!;
        Assert.Equal(101.75, input.DeliveriesKg![6]); Assert.Equal(10, input.DeliveriesKg[12]); Assert.Equal(20, input.DeliveriesKg[16]); Assert.Equal("999", model.Delivery6);
        model.UseHistory = false; Assert.Null(model.Result); Assert.True(await model.Calculate(api, default));
        input = System.Text.Json.JsonSerializer.Deserialize<TetrapodInput>(handler.Bodies[1], json)!; Assert.Equal(999, input.DeliveriesKg![6]); Assert.Null(input.DeliveriesKg[12]);
    }
    [Fact]
    public async Task Failed_history_save_keeps_rows_and_remove_retry_persists_empty_history()
    {
        var store = new MemoryTetrapodDeliveryStore(); var model = new TetrapodViewModel(101, store) { DeliveryAccess = true };
        await model.LoadDeliveries(default); model.NewDelivery6 = "12,5"; Assert.True(model.AddDelivery()); var row = Assert.Single(model.Deliveries);
        store.FailNextWrite = true; Assert.False(await model.SaveDeliveries(default)); Assert.True(model.HistoryDirty); Assert.Equal(row, Assert.Single(model.Deliveries)); Assert.True(model.CanSaveDeliveries);
        Assert.True(await model.SaveDeliveries(default)); Assert.False(model.HistoryDirty); model.UseHistory = false; Assert.False(model.Dirty); Assert.Single(store.Users[101]);
        Assert.True(model.RemoveDelivery(row.Entry.Id)); Assert.True(model.Dirty); Assert.True(await model.SaveDeliveries(default)); Assert.Empty(store.Users[101]); Assert.False(model.Dirty);
        var restored = new TetrapodViewModel(101, store) { DeliveryAccess = true }; await restored.LoadDeliveries(default); Assert.Empty(restored.Deliveries);
    }
    [Fact]
    public async Task Loaded_and_saved_history_is_clean_while_actual_drafts_remain_dirty()
    {
        var store = new MemoryTetrapodDeliveryStore(); var entry = new TetrapodDelivery(Guid.NewGuid(), DateTimeOffset.Parse("2026-10-01T09:30:00Z"), 12.5, 2, 3);
        store.Users[101] = [entry]; var model = new TetrapodViewModel(101, store) { DeliveryAccess = true };
        Assert.True(await model.LoadDeliveries(default)); Assert.True(model.UseHistory); Assert.Equal(entry, Assert.Single(model.Deliveries).Entry);
        Assert.False(model.HistoryDirty); Assert.False(model.LocalDirty); Assert.False(model.Dirty);
        model.UseHistory = false; Assert.False(model.Dirty); model.UseHistory = true; Assert.False(model.Dirty);
        model.NewDelivery6 = "4,5"; Assert.True(model.HasDeliveryDraft); Assert.True(model.LocalDirty); Assert.True(model.Dirty);
        Assert.True(model.AddDelivery()); Assert.True(model.HistoryDirty); Assert.True(model.Dirty);
        Assert.True(await model.SaveDeliveries(default)); Assert.True(model.UseHistory); Assert.False(model.HistoryDirty); Assert.False(model.LocalDirty); Assert.False(model.Dirty);
        var restored = new TetrapodViewModel(101, store) { DeliveryAccess = true }; Assert.True(await restored.LoadDeliveries(default)); Assert.Equal(2, restored.Deliveries.Count); Assert.False(restored.Dirty);
        restored.Planned = "10"; Assert.True(restored.Dirty); restored.Planned = ""; Assert.False(restored.Dirty);
        restored.Delivery12 = "0"; Assert.True(restored.Dirty); restored.Delivery12 = ""; Assert.False(restored.Dirty);
        restored.Completed = "1"; Assert.True(restored.Dirty); restored.Completed = "0"; Assert.False(restored.Dirty);
        Assert.True(restored.RemoveDelivery(entry.Id)); Assert.True(restored.HistoryDirty); Assert.True(restored.Dirty);
    }
    [Fact]
    public async Task Unreadable_history_preserves_draft_and_cannot_overwrite_existing_file()
    {
        var store = new MemoryTetrapodDeliveryStore { FailNextRead = true };
        var model = new TetrapodViewModel(101, store) { DeliveryAccess = true, UseHistory = true, Planned = "5", NewDelivery6 = "25" };
        Assert.False(await model.LoadDeliveries(default)); Assert.False(model.CanSaveDeliveries); Assert.False(model.AddDelivery()); Assert.Equal("25", model.NewDelivery6);
        using var api = new CrmClient(new("http://localhost/"), new EditorTests.Responses()); Assert.False(await model.Calculate(api, default));
        Assert.Equal("5", model.Planned); Assert.True(await model.LoadDeliveries(default)); Assert.True(model.AddDelivery());
        model.NewDelivery12 = "-1"; Assert.False(model.AddDelivery()); Assert.Equal("-1", model.NewDelivery12); Assert.Single(model.Deliveries);
        model.DeliveryAccess = false; Assert.False(model.CanManageDeliveries); Assert.False(await model.SaveDeliveries(default));
    }
    [Fact]
    public async Task Explicit_empty_history_sends_zero_after_last_delivery_is_removed()
    {
        var model = new TetrapodViewModel(101, new MemoryTetrapodDeliveryStore()) { DeliveryAccess = true, Planned = "10", Delivery6 = "999", Delivery12 = "888", Delivery16 = "777" };
        await model.LoadDeliveries(default); Assert.False(model.UseHistory); model.NewDelivery6 = "1"; model.AddDelivery(); Assert.True(model.UseHistory);
        model.RemoveDelivery(model.Deliveries[0].Entry.Id); await model.SaveDeliveries(default);
        var handler = new EditorTests.Responses(); handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(Result()))); using var api = new CrmClient(new("http://localhost/"), handler);
        Assert.True(await model.Calculate(api, default)); var input = System.Text.Json.JsonSerializer.Deserialize<TetrapodInput>(handler.Bodies.Single(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.All(input.DeliveriesKg!, entry => Assert.Equal(0, entry.Value)); Assert.Equal("999", model.Delivery6);
    }
    [Fact]
    public async Task Atomic_delivery_store_is_user_isolated_and_failed_writes_preserve_previous_file()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tetrapod-history-test-" + Guid.NewGuid());
        try
        {
            var store = new TetrapodDeliveryStore(path); var row = new TetrapodDelivery(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, 2, 3);
            await store.Write(101, [row], default); Assert.Equal(row, Assert.Single(await store.Read(101, default))); Assert.Empty(await store.Read(202, default));
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => store.Write(101, [row with { Kg6 = -1 }], default));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Write(101, [row with { Kg6 = 999 }], cancellation.Token));
            Assert.Equal(row, Assert.Single(await store.Read(101, default))); Assert.Single(System.IO.Directory.GetFiles(path));
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(path, "user-202.json"), "broken");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.Read(202, default));
            Assert.Equal("broken", await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(path, "user-202.json")));
        }
        finally { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
    }

}

internal sealed class MemoryTetrapodDeliveryStore : ITetrapodDeliveryStore
{
    internal Dictionary<long, TetrapodDelivery[]> Users { get; } = [];
    internal bool FailNextWrite;
    internal bool FailNextRead;
    public Task<TetrapodDelivery[]> Read(long userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailNextRead) { FailNextRead = false; throw new System.IO.IOException("Synthetic read failure"); }
        return Task.FromResult(Users.TryGetValue(userId, out var entries) ? entries.ToArray() : []);
    }
    public Task Write(long userId, TetrapodDelivery[] entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailNextWrite) { FailNextWrite = false; throw new System.IO.IOException("Synthetic write failure"); }
        Users[userId] = entries.ToArray(); return Task.CompletedTask;
    }
}
