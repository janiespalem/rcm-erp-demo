using System.Text.RegularExpressions;

namespace Rcm.Host;

public static partial class DesktopDownloads
{
    public static void MapDesktopDownloads(this WebApplication app)
    {
        app.MapGet("/desktop", (IConfiguration configuration) =>
            ReleaseDirectory(configuration) is null ? Results.NotFound() : Results.Content(Page, "text/html; charset=utf-8"));
        app.MapGet("/desktop/{fileName}", (string fileName, IConfiguration configuration, HttpContext context) =>
        {
            var directory = ReleaseDirectory(configuration);
            if (directory is null || !AllowedFileName(fileName)) return Results.NotFound();
            var file = new FileInfo(Path.Combine(directory, fileName));
            if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file.FullName, "application/octet-stream", fileName, enableRangeProcessing: true);
        });
    }

    private static string? ReleaseDirectory(IConfiguration configuration)
    {
        var directory = configuration["Desktop:ReleasePath"];
        return !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory) && Directory.Exists(directory)
            ? directory : null;
    }

    private static bool AllowedFileName(string name) => name is "FactoryFlow-Setup.exe" or "releases.win.json"
        or "releases.win.json.sig" or "SHA256SUMS.txt" || ArtifactName().IsMatch(name);

    [GeneratedRegex(@"\AFactoryFlow-[A-Za-z0-9][A-Za-z0-9._-]*(\.(nupkg|zip)|-Setup\.exe)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactName();

    private const string Page = """
        <!doctype html>
        <html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>FactoryFlow ERP — aplikacja Windows</title>
        <style>
        body{margin:0;background:#f4f3ea;color:#273e35;font:17px/1.6 system-ui,sans-serif}
        main{max-width:620px;margin:10vh auto;padding:36px;background:white;border-radius:16px}
        h1{line-height:1.15}a{display:inline-block;padding:14px 22px;background:#123e42;color:white;border-radius:8px;text-decoration:none;font-weight:650}
        small{display:block;margin-top:28px;color:#4d5a65}@media(max-width:680px){main{margin:24px;padding:24px}}
        </style></head><body><main>
        <h1>FactoryFlow ERP na Windows</h1>
        <p>Zainstaluj aplikację, aby korzystać z CRM.</p>
        <a href="/desktop/FactoryFlow-Setup.exe">Pobierz instalator Windows</a>
        <p>Instalator są dostępne przez zwykłe połączenie internetowe.</p>
        <small>Instalator pilota nie ma jeszcze potwierdzonego podpisu wydawcy. Windows może wyświetlić ostrzeżenie; pobieraj aplikację wyłącznie z tej strony.</small>
        </main></body></html>
        """;
}
