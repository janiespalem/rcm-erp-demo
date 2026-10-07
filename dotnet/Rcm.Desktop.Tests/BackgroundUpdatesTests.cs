using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class BackgroundUpdatesTests
{
    [Fact]
    public async Task Ready_update_does_not_download_again_and_dirty_form_prevents_apply()
    {
        using var client = new Client(); var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.True(model.Ready); Assert.False(model.Busy); Assert.Equal("0.4.0", model.Version);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.Equal(1, client.Checks); Assert.Equal(1, client.Downloads);
        Assert.False(await model.ApplyIfSafe(false, CancellationToken.None)); Assert.Equal(0, client.Applies); Assert.True(model.Ready);
        Assert.Contains("Zapisz dane", model.Status);
        Assert.True(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(1, client.Applies);
        Assert.False(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(1, client.Applies);
    }

    [Theory]
    [InlineData(false, "0.4.0")]
    [InlineData(true, null)]
    public async Task Uninstalled_or_current_application_stays_silent(bool installed, string? available)
    {
        using var client = new Client { IsInstalled = installed, Available = available };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.False(model.Ready); Assert.False(model.Busy); Assert.Equal("", model.Status);
        Assert.False(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(0, client.Downloads); Assert.Equal(0, client.Applies);
        Assert.Equal(installed ? 1 : 0, client.Checks);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("network")]
    [InlineData("version")]
    public async Task Invalid_or_unavailable_update_cannot_apply_and_next_poll_can_retry(string failure)
    {
        using var client = new Client();
        client.CheckFailure = failure switch { "signature" => new CryptographicException(), "network" => new HttpRequestException(), _ => null };
        if (failure == "version") client.Available = " ";
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.False(model.Ready); Assert.False(model.Busy); Assert.Equal("", model.Version);
        Assert.False(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(0, client.Applies); Assert.Equal(0, client.Downloads);
        client.CheckFailure = null; client.Available = "0.4.0";
        await model.CheckAndDownload(CancellationToken.None);
        Assert.True(model.Ready); Assert.Equal(2, client.Checks); Assert.Equal(1, client.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_download_never_marks_package_ready(bool cancelled)
    {
        using var client = new Client { DownloadFailure = cancelled ? new OperationCanceledException() : new CryptographicException() };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.False(model.Ready); Assert.False(model.Busy); Assert.False(await model.ApplyIfSafe(true, CancellationToken.None));
        Assert.Equal(0, client.Applies); Assert.Equal("", model.Version);
        client.DownloadFailure = null;
        await model.CheckAndDownload(CancellationToken.None);
        Assert.True(model.Ready); Assert.Equal(2, client.Downloads);
    }

    [Fact]
    public async Task Already_cancelled_poll_does_not_check_or_download()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var client = new Client(); var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(cancellation.Token);
        Assert.False(model.Ready); Assert.False(model.Busy); Assert.False(await model.ApplyIfSafe(true, CancellationToken.None));
        Assert.Equal(0, client.Checks); Assert.Equal(0, client.Downloads); Assert.Equal(0, client.Applies);
    }

    [Fact]
    public async Task Cancelled_token_cannot_enable_apply_even_when_downloader_ignores_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new Client { DownloadAction = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; } };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(cancellation.Token);
        Assert.False(model.Ready); Assert.False(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(0, client.Applies);
    }

    [Fact]
    public async Task In_progress_download_blocks_other_polls_and_apply()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new Client { DownloadAction = (_, ct) => completion.Task.WaitAsync(ct) };
        var model = new BackgroundUpdates(client);
        var first = model.CheckAndDownload(CancellationToken.None);
        Assert.True(model.Busy); Assert.False(model.Ready);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.Equal(1, client.Checks); Assert.Equal(1, client.Downloads); Assert.False(await model.ApplyIfSafe(true, CancellationToken.None));
        completion.SetResult(); await first;
        Assert.True(model.Ready); Assert.False(model.Busy);
    }

    [Fact]
    public async Task Apply_failure_keeps_current_application_running_and_allows_download_retry()
    {
        using var client = new Client { ApplyFailure = new CryptographicException() };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.False(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.False(model.Ready); Assert.Contains("bieżącej wersji", model.Status);
        client.ApplyFailure = null;
        await model.CheckAndDownload(CancellationToken.None);
        Assert.True(await model.ApplyIfSafe(true, CancellationToken.None)); Assert.Equal(2, client.Applies);
    }

    [Fact]
    public async Task Applying_update_blocks_double_apply_and_background_poll()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new Client { ApplyAction = ct => completion.Task.WaitAsync(ct) };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        var first = model.ApplyIfSafe(true, CancellationToken.None);
        Assert.True(model.Busy);
        Assert.False(await model.ApplyIfSafe(true, CancellationToken.None));
        await model.CheckAndDownload(CancellationToken.None);
        Assert.Equal(1, client.Applies); Assert.Equal(1, client.Checks); Assert.Equal(1, client.Downloads);
        completion.SetResult();
        Assert.True(await first); Assert.False(model.Busy); Assert.False(model.Ready);
    }

    [Fact]
    public async Task Cancelled_apply_keeps_application_running_and_next_poll_can_retry()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new Client { ApplyAction = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var model = new BackgroundUpdates(client);
        await model.CheckAndDownload(CancellationToken.None);
        var pending = model.ApplyIfSafe(true, cancellation.Token);
        cancellation.Cancel();
        Assert.False(await pending); Assert.False(model.Busy); Assert.False(model.Ready);
        await model.CheckAndDownload(CancellationToken.None);
        Assert.True(model.Ready); Assert.Equal(2, client.Downloads);
    }

    [Fact]
    public async Task Download_progress_posts_to_ui_context_and_stale_progress_cannot_replace_ready_notice()
    {
        var context = new QueuedContext();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int>? report = null;
        using var client = new Client { DownloadAction = (progress, _) => { report = progress; return completion.Task; } };
        var model = new BackgroundUpdates(client);
        var previous = SynchronizationContext.Current;
        Task operation;
        try { SynchronizationContext.SetSynchronizationContext(context); operation = model.CheckAndDownload(CancellationToken.None); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Task.Run(() => report!(45));
        Assert.DoesNotContain("45%", model.Status);
        Assert.True(context.Pump()); Assert.Contains("45%", model.Status);
        completion.SetResult();
        for (var i = 0; !operation.IsCompleted && i < 100; i++) { context.Pump(); await Task.Delay(5); }
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        var readyNotice = model.Status;
        await Task.Run(() => report!(99));
        Assert.True(context.Pump()); Assert.Equal(readyNotice, model.Status); Assert.True(model.Ready);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));
        public bool Pump()
        {
            if (!queue.TryDequeue(out var next)) return false;
            var previous = Current;
            try { SetSynchronizationContext(this); next.Callback(next.State); }
            finally { SetSynchronizationContext(previous); }
            return true;
        }
    }

    private sealed class Client : IBackgroundUpdateClient
    {
        public bool IsInstalled { get; init; } = true;
        public string? Available { get; set; } = "0.4.0";
        public Exception? CheckFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Exception? ApplyFailure { get; set; }
        public Func<Action<int>, CancellationToken, Task>? DownloadAction { get; init; }
        public Func<CancellationToken, Task>? ApplyAction { get; init; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }
        public Task<string?> Check(CancellationToken ct)
        {
            Checks++;
            if (CheckFailure is not null) throw CheckFailure;
            return Task.FromResult(Available);
        }
        public Task Download(Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            if (DownloadFailure is not null) throw DownloadFailure;
            return DownloadAction?.Invoke(progress, ct) ?? Task.CompletedTask;
        }
        public Task Apply(CancellationToken ct)
        {
            Applies++; if (ApplyFailure is not null) throw ApplyFailure;
            return ApplyAction?.Invoke(ct) ?? Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
