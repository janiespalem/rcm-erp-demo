namespace Rcm.Desktop;

internal static class StartupUpdates
{
    public static Task<string> Run(StartupWindow window)
        => Task.FromResult("Demo lokalne. Automatyczne aktualizacje są wyłączone.");
}
