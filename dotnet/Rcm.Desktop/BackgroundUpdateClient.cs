namespace Rcm.Desktop;

internal sealed class BackgroundUpdateClient : IBackgroundUpdateClient
{
    public BackgroundUpdateClient(Func<bool> canRestart) { }
    public bool IsInstalled => false;
    public Task<string?> Check(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task Download(Action<int> progress, CancellationToken ct) => Task.FromException(new NotSupportedException("Aktualizacje demo są wyłączone."));
    public Task Apply(CancellationToken ct) => Task.FromException(new NotSupportedException("Aktualizacje demo są wyłączone."));
    public void Dispose() { }
}
