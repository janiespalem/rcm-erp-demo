using System.Security.Cryptography;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeTemplates
{
    private const long DrawingLimit = 25L * 1024 * 1024;
    private static readonly SemaphoreSlim DrawingSlots = new(2);
    private string UploadRoot()
    {
        var configured = configuration["Catalog:UploadRoot"] ?? configuration["Orders:UploadRoot"];
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured)) throw new CrmFault(503, "Katalog rysunków nie został skonfigurowany.");
        var root = Path.GetFullPath(configured); CheckComponents(root); return root;
    }
    private static void CheckComponents(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new CrmFault(403, "Niedozwolone dowiązanie w ścieżce rysunku.");
        }
    }
    private string AttachmentPath(string stored)
    {
        var root = UploadRoot(); var parts = stored.Split('/');
        if (parts.Length < 4 || parts[0] != "uploads" || parts[1] != "templates" || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains(':') || p.Any(char.IsControl))
            || !stored.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new CrmFault(403, "Niedozwolona ścieżka rysunku.");
        var path = Path.Combine([root, ..parts.Skip(1)]); CheckComponents(path); return path;
    }
    private static async Task<byte[]> ReadPdf(Stream source, long? length, CancellationToken ct)
    {
        if (length > DrawingLimit) throw new CrmFault(413, "Rysunek przekracza limit 25 MiB.");
        using var output = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > DrawingLimit) throw new CrmFault(413, "Rysunek przekracza limit 25 MiB.");
            output.Write(buffer, 0, read);
        }
        if (length is not null && output.Length != length) throw new CrmFault(422, "Przesyłanie pliku zostało przerwane.");
        var bytes = output.ToArray();
        if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new CrmFault(415, "Zawartość pliku nie jest PDF.");
        return bytes;
    }
    private async Task<(byte[] Bytes, string Name)> Drawing(Npgsql.NpgsqlConnection db, long id, CancellationToken ct)
    {
        var stored = (await Read<string>(db, "SELECT to_json(drawing_path) FROM public.product_templates WHERE id=@id", ct, P("id", id))).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(stored)) throw new CrmFault(404, "Brak rysunku.");
        try
        {
            var path = AttachmentPath(stored);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            CheckComponents(path);
            var name = Path.GetFileName(path);
            var logical = name.Length == 101 && name[64] == '_' ? $"rysunek_{id}.pdf" : name;
            return (await ReadPdf(input, input.Length, ct), logical);
        }
        catch (FileNotFoundException) { throw new CrmFault(404, "Plik rysunku nie istnieje."); }
        catch (DirectoryNotFoundException) { throw new CrmFault(404, "Plik rysunku nie istnieje."); }
        catch (UnauthorizedAccessException) { throw new CrmFault(403, "Brak dostępu do rysunku."); }
        catch (IOException) { throw new CrmFault(503, "Nie można odczytać rysunku."); }
    }
    public async Task<IResult> DownloadDrawing(long id, CancellationToken ct)
    {
        await using var db = await Open(ct); await Get(db, id, ct);
        var drawing = await Drawing(db, id, ct);
        return new TemplateDownload(Results.File(drawing.Bytes, "application/pdf", $"rysunek_{id}.pdf"));
    }
    public async Task<ProductTemplateDto> UploadDrawing(LegacyUser actor, long id, Guid requestId, long expectedVersion,
        string filename, string? mime, Stream content, long? contentLength, CancellationToken ct)
    {
        RequireTech(actor); RequireVersion(expectedVersion);
        if (requestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        if (mime is not ("application/pdf" or "application/octet-stream")) throw new CrmFault(415, "Tylko PDF.");
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 200 || filename.Any(char.IsControl) || filename.IndexOfAny(['/', '\\', ':']) >= 0
            || !filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw CrmFault.Invalid("filename", "Podaj nazwę PDF bez ścieżki (do 200 znaków).");
        string? created = null; string? stored = null;
        try
        {
            return await Bounded(DrawingSlots, async token =>
            {
                var bytes = await ReadPdf(content, contentLength, token);
                var digest = Convert.ToHexString(SHA256.HashData(bytes));
                return await Change(actor, requestId, "template_drawing_upload", id, new { expectedVersion, filename, digest }, async db =>
                {
                    await Version(db, id, expectedVersion, token);
                    var root = UploadRoot(); var directory = Path.Combine(root, "templates", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(directory); CheckComponents(directory);
                    var name = digest.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N") + ".pdf";
                    created = Path.Combine(directory, name); stored = $"uploads/templates/{id}/{name}";
                    await using (var output = new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    { CheckComponents(created); await output.WriteAsync(bytes, token); await output.FlushAsync(token); }
                    return (await Read<ProductTemplateDto>(db, "UPDATE public.product_templates SET drawing_path=@path WHERE id=@id RETURNING " + TemplateJson, token, P("path", stored), P("id", id)))[0];
                }, token);
            }, ct);
        }
        catch
        {
            if (created is not null && stored is not null) await CleanupUncommittedFile(created, stored, requestId);
            throw;
        }
    }
    private async Task CleanupUncommittedFile(string file, string stored, Guid request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await using var db = await Open(timeout.Token);
            await using var tx = await db.BeginTransactionAsync(timeout.Token);
            await Execute(db, "SET LOCAL lock_timeout='5s'", timeout.Token);
            var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("catalog:" + request)));
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", timeout.Token, P("key", key));
            var retained = (await Read<bool>(db, """
                SELECT to_json(EXISTS(SELECT 1 FROM public.product_templates WHERE drawing_path=@path)
                    OR EXISTS(SELECT 1 FROM public.catalog_command_receipts WHERE request_id=@request))
                """, timeout.Token, P("path", stored), P("request", request.ToString())))[0];
            if (!retained) { CheckComponents(file); File.Delete(file); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Npgsql.NpgsqlException or OperationCanceledException or CrmFault) { }
    }
    public Task<TemplateDrawingPreview> PreviewDrawing(LegacyUser actor, long id, CancellationToken ct)
    {
        RequireTech(actor);
        return Bounded(DrawingSlots, async token =>
        {
            await using var db = await Open(token); await Get(db, id, token); var drawing = await Drawing(db, id, token);
            return await Task.Run(() => DrawingTextExtractor.Extract(drawing.Bytes, drawing.Name, token), token);
        }, ct);
    }
    public Task<ProductTemplateDto> ApplyDrawing(LegacyUser actor, long id, ApplyTemplateDrawing c, CancellationToken ct)
    {
        RequireTech(actor); RequireVersion(c.ExpectedVersion);
        return Bounded(DrawingSlots, token => Change(actor, c.RequestId, "template_drawing_apply", id, c, async db =>
        {
            var row = await Version(db, id, c.ExpectedVersion, token); var drawing = await Drawing(db, id, token);
            var preview = await Task.Run(() => DrawingTextExtractor.Extract(drawing.Bytes, drawing.Name, token), token);
            var draft = new ProductTemplateDraft(string.IsNullOrEmpty(preview.Name) ? row.Name : preview.Name, row.Category,
                preview.Operations.GetArrayLength() > 0 ? preview.Operations : row.Operations,
                preview.Materials.GetArrayLength() > 0 ? preview.Materials : row.Materials, row.Instructions, row.Machines, row.BasePricePln, row.MarginPct,
                row.ProjectCode, row.PositionNumber, $"Odczytano z PDF: {drawing.Name}\nRysunek: {preview.DrawingNumber ?? "-"}\nMasa: {preview.MassKg?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} kg");
            Validate(draft);
            return (await Read<ProductTemplateDto>(db, "UPDATE public.product_templates SET name=@name,operations_json=@operations::json,materials_json=@materials::json,notes=@notes WHERE id=@id RETURNING " + TemplateJson, token,
                P("name", draft.Name), P("operations", draft.Operations.GetRawText()), P("materials", draft.Materials.GetRawText()), P("notes", draft.Notes), P("id", id)))[0];
        }, token), ct);
    }
    private static async Task<T> Bounded<T>(SemaphoreSlim slots, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var entered = false;
        try
        {
            if (!await slots.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)) throw new CrmFault(503, "Trwa przetwarzanie innych PDF. Ponów operację.");
            entered = true; return await action(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Przetwarzanie PDF trwało zbyt długo. Ponów operację."); }
        catch (IOException) { throw new CrmFault(503, "Nie można zapisać lub odczytać PDF. Ponów operację z tym samym identyfikatorem."); }
        catch (UnauthorizedAccessException) { throw new CrmFault(503, "Katalog rysunków jest niedostępny."); }
        finally { if (entered) slots.Release(); }
    }
    private sealed class TemplateDownload(IResult result) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.XContentTypeOptions = "nosniff"; context.Response.Headers.ContentSecurityPolicy = "sandbox";
            return result.ExecuteAsync(context);
        }
    }
}
