using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    private const long AttachmentLimit = 100L * 1024 * 1024;
    private static readonly Dictionary<string, string> AttachmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".dxf"] = "image/vnd.dxf", [".dwg"] = "image/vnd.dwg",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    public async Task<OrderAttachmentDto> UploadAttachment(LegacyUser actor, long orderId, Guid requestId,
        string filename, Stream content, long? contentLength, CancellationToken ct)
    {
        if (actor.Id <= 0 || actor.Role is not ("biuro" or "technolog" or "ceo"))
            throw new CrmFault(403, "Brak uprawnień do tej operacji.");
        if (requestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 200 || filename.Any(char.IsControl)
            || filename.IndexOfAny(['/', '\\', ':']) >= 0 || filename is "." or "..")
            throw CrmFault.Invalid("filename", "Podaj nazwę pliku bez ścieżki (do 200 znaków).");
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!AttachmentTypes.TryGetValue(extension, out var mime)) throw new CrmFault(415, "Niedozwolony typ pliku.");
        if (contentLength > AttachmentLimit) throw new CrmFault(413, "Plik przekracza limit 100 MiB.");
        var root = UploadRoot();
        var temporary = Path.Combine(root, $".rcm-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; int count;
                while ((count = await content.ReadAsync(buffer, ct)) != 0)
                {
                    size += count;
                    if (size > AttachmentLimit) throw new CrmFault(413, "Plik przekracza limit 100 MiB.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (contentLength is not null && contentLength != size)
                    throw new CrmFault(422, "Przesyłanie pliku zostało przerwane. Ponów operację.");
                await output.FlushAsync(ct);
                output.Position = 0;
                await VerifyAttachment(output, extension, ct);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            return await ResourceCommand(actor, orderId, null, requestId, "file_added", new { filename, size, digest }, async db =>
            {
                var directory = Path.Combine(root, orderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                CheckAttachmentComponents(directory, 403);
                var storedName = Guid.NewGuid().ToString("N") + extension;
                var movedPath = Path.Combine(directory, storedName);
                // Retain a moved file if commit confirmation is lost; deleting it could destroy committed data.
                File.Move(temporary, movedPath);
                var result = (await Read<OrderAttachmentDto>(db, """
                    INSERT INTO public.order_attachments(order_id,filename,stored_path,size_bytes,mime_type,uploaded_by,uploaded_at)
                    VALUES(@order,@filename,@path,@size,@mime,@actor,timezone('UTC',now()))
                    RETURNING row_to_json(order_attachments)
                    """, ct, P("order", orderId), P("filename", filename), P("path", $"uploads/{orderId}/{storedName}"),
                    P("size", size), P("mime", mime), P("actor", actor.Role)))[0];
                await Event(db, actor, orderId, "file_added", null, null, filename, ct);
                return result;
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CrmFault(503, "Nie można zapisać załącznika. Ponów operację z tym samym plikiem."); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public async Task<RemovedOrderAttachment> RemoveAttachment(LegacyUser actor, long orderId, long attachmentId,
        RemoveOrderAttachment command, CancellationToken ct)
    {
        RequireEdit(actor, true);
        var result = await ResourceCommand(actor, orderId, attachmentId, command.RequestId, "file_removed", attachmentId, async db =>
        {
            var file = (await Read<AttachmentFile>(db,
                "SELECT row_to_json(a) FROM public.order_attachments a WHERE order_id=@order AND id=@id",
                ct, P("order", orderId), P("id", attachmentId))).SingleOrDefault()
                ?? throw new CrmFault(404, "Załącznik nie znaleziony.");
            AttachmentPath(file.StoredPath);
            await Execute(db, "DELETE FROM public.order_attachments WHERE order_id=@order AND id=@id",
                ct, P("order", orderId), P("id", attachmentId));
            await Event(db, actor, orderId, "file_removed", null, null, file.Filename, ct);
            var shared = (await Read<bool>(db,
                "SELECT to_json(EXISTS(SELECT 1 FROM public.order_attachments WHERE stored_path=@path))",
                ct, P("path", file.StoredPath)))[0];
            return new RemovedAttachmentFile(attachmentId, file.StoredPath, !shared);
        }, ct);
        try { if (result.DeleteFile) File.Delete(AttachmentPath(result.StoredPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CrmFault) { }
        return new(result.Id);
    }

    private string UploadRoot()
    {
        var root = configuration["Orders:UploadRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new CrmFault(503, "Katalog załączników nie został skonfigurowany.");
        root = Path.GetFullPath(root);
        try { CheckAttachmentComponents(root, 503); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CrmFault(503, "Katalog załączników jest niedostępny."); }
        return root;
    }

    private string AttachmentPath(string storedPath)
    {
        var root = UploadRoot();
        var parts = storedPath.Split('/');
        if (parts.Length < 2 || parts[0] != "uploads" || parts.Any(p => p.Length == 0 || p is "." or ".."
            || p.Contains('\\') || p.Contains(':') || p.Any(char.IsControl)))
            throw new CrmFault(403, "Niedozwolona ścieżka załącznika.");
        var path = Path.Combine([root, ..parts.Skip(1)]);
        try { CheckAttachmentComponents(path, 403); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return path;
    }

    private sealed record RemovedAttachmentFile(long Id, string StoredPath, bool DeleteFile);

    public async Task<OrderAttachmentDto[]> Attachments(long orderId, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await GetOrder(db, orderId, ct);
        return await Read<OrderAttachmentDto>(db,
            "SELECT row_to_json(a) FROM public.order_attachments a WHERE order_id=@id ORDER BY uploaded_at,id",
            ct, P("id", orderId));
    }

    public async Task<IResult> DownloadAttachment(long orderId, long attachmentId, CancellationToken ct)
    {
        AttachmentFile attachment;
        await using (var db = await Open(ct))
        {
            await GetOrder(db, orderId, ct);
            attachment = (await Read<AttachmentFile>(db,
                "SELECT row_to_json(a) FROM public.order_attachments a WHERE order_id=@order AND id=@id",
                ct, P("order", orderId), P("id", attachmentId))).SingleOrDefault()
                ?? throw new CrmFault(404, "Załącznik nie znaleziony.");
        }
        var root = configuration["Orders:UploadRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new CrmFault(503, "Katalog załączników nie został skonfigurowany.");
        var extension = Path.GetExtension(attachment.Filename).ToLowerInvariant();
        if (!AttachmentTypes.TryGetValue(extension, out var mime)) throw new CrmFault(415, "Niedozwolony typ pliku.");
        FileStream? stream = null;
        try
        {
            root = Path.GetFullPath(root);
            CheckAttachmentComponents(root, 503);
            var parts = attachment.StoredPath.Split('/');
            if (parts.Length < 2 || parts[0] != "uploads" || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains(':') || p.Any(char.IsControl)))
                throw new CrmFault(403, "Niedozwolona ścieżka załącznika.");
            var path = Path.Combine([root, ..parts.Skip(1)]);
            CheckAttachmentComponents(path, 403);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            CheckAttachmentComponents(path, 403);
            if (stream.Length > AttachmentLimit) throw new CrmFault(413, "Plik przekracza limit 100 MiB.");
            await VerifyAttachment(stream, extension, ct);
            stream.Position = 0;
            var filename = new string(attachment.Filename.Where(c => !char.IsControl(c) && c is not '/' and not '\\').ToArray());
            if (string.IsNullOrWhiteSpace(filename)) filename = "plik" + extension;
            return new AttachmentDownload(Results.File(stream, mime, filename, enableRangeProcessing: false));
        }
        catch (Exception e)
        {
            if (stream is not null) await stream.DisposeAsync();
            throw e switch
            {
                FileNotFoundException or DirectoryNotFoundException => new CrmFault(404, "Plik nie istnieje."),
                UnauthorizedAccessException => new CrmFault(403, "Brak dostępu do załącznika."),
                IOException => new CrmFault(503, "Nie można odczytać załącznika."),
                ArgumentException or NotSupportedException => new CrmFault(403, "Niedozwolona ścieżka załącznika."),
                _ => e
            };
        }
    }

    private static void CheckAttachmentComponents(string path, int status)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CrmFault(status, "Niedozwolone dowiązanie w ścieżce załącznika.");
        }
    }

    private static async Task VerifyAttachment(Stream stream, string extension, CancellationToken ct)
    {
        var head = new byte[Math.Min(1024, stream.Length)];
        await stream.ReadExactlyAsync(head, ct);
        var valid = extension switch
        {
            ".pdf" => head.AsSpan().StartsWith("%PDF-"u8),
            ".png" => head.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => head.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            ".dwg" => head.Length >= 6 && head.AsSpan().StartsWith("AC10"u8) && head[4] is >= 48 and <= 57 && head[5] is >= 48 and <= 57,
            ".dxf" => head.AsSpan().StartsWith("AutoCAD Binary DXF\r\n\x1a\0"u8)
                || Encoding.ASCII.GetString(head).TrimStart(' ', '\t', '\r', '\n', '\v', '\f').Replace("\r\n", "\n").StartsWith("0\nSECTION", StringComparison.Ordinal),
            ".docx" or ".xlsx" => ValidOfficeArchive(stream, extension),
            _ => false
        };
        if (!valid) throw new CrmFault(415, "Zawartość pliku nie pasuje do rozszerzenia.");
    }

    private static bool ValidOfficeArchive(Stream stream, string extension)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return zip.GetEntry("[Content_Types].xml") is not null
                && zip.GetEntry(extension == ".docx" ? "word/document.xml" : "xl/workbook.xml") is not null;
        }
        catch (InvalidDataException) { return false; }
    }

    private sealed record AttachmentFile(string Filename, string StoredPath);
    private sealed class AttachmentDownload(IResult file) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "sandbox";
            return file.ExecuteAsync(context);
        }
    }
}
