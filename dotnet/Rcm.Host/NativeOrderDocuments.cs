using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    private const long DocumentLimit = 100L * 1024 * 1024;
    private static readonly SemaphoreSlim DocumentSlots = new(2);

    public async Task<IResult> DownloadDocument(long orderId, string kind, CancellationToken ct)
    {
        if (kind is not ("arkusz" or "oferta" or "operations")) throw new CrmFault(404, "Dokument nie znaleziony.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        if (!await DocumentSlots.WaitAsync(TimeSpan.FromSeconds(5), token))
            throw new CrmFault(503, "Trwa generowanie innych dokumentów. Ponów pobieranie.");
        try
        {
            OrderDocumentData data;
            List<string> paths = [];
            await using (var db = await Open(token))
            {
                await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
                var order = (await Read<JsonElement>(db, OrderSelect + " WHERE id=@id", token, P("id", orderId))).SingleOrDefault();
                if (order.ValueKind != JsonValueKind.Object) throw new CrmFault(404, "Zlecenie nie znalezione.");
                var template = (await Read<JsonElement>(db, "SELECT row_to_json(t) FROM public.product_templates t WHERE id=@id", token,
                    P("id", order.TryGetProperty("template_id", out var templateId) && templateId.ValueKind == JsonValueKind.Number && templateId.TryGetInt64(out var id) ? id : 0L))).SingleOrDefault();
                var quote = (await Read<JsonElement>(db, "SELECT row_to_json(q) FROM public.quotes q WHERE order_id=@id", token, P("id", orderId))).SingleOrDefault();
                var settings = (await Read<DocumentSetting>(db, "SELECT row_to_json(s) FROM public.settings s WHERE key LIKE 'company_%' OR key='vat_rate'", token))
                    .ToDictionary(s => s.Key, s => s.Value);
                var company = new Dictionary<string, string>
                {
                    ["name"] = Setting("company_name", "FactoryFlow Demo"), ["address"] = Setting("company_address", "Synthetic Avenue 1, Demo City"),
                    ["nip"] = Setting("company_nip", "DEMO-NOT-A-TAX-ID"), ["regon"] = Setting("company_regon", "DEMO"),
                    ["tagline"] = Setting("company_tagline", "Synthetic demonstration")
                };
                string Setting(string key, string fallback) => settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;
                var vat = .23;
                if (kind == "oferta" && (!double.TryParse(Setting("vat_rate", "0.23"), NumberStyles.Float, CultureInfo.InvariantCulture, out vat)
                    || !double.IsFinite(vat) || vat is < 0 or > 1)) throw new CrmFault(422, "Nieprawidłowa stawka VAT.");
                data = new(order, template, quote, company, vat, Today);
                if (kind != "oferta")
                {
                    var drawing = OrderDocumentRenderer.Text(template, "drawing_path");
                    if (drawing != "") paths.Add(AttachmentPath(drawing));
                    var attachments = await Read<DocumentAttachment>(db,
                        "SELECT row_to_json(a) FROM public.order_attachments a WHERE order_id=@id AND mime_type='application/pdf' ORDER BY uploaded_at,id",
                        token, P("id", orderId));
                    paths.AddRange(attachments.Select(a => AttachmentPath(a.StoredPath)));
                }
                await tx.CommitAsync(token);
            }
            List<byte[]> drawings = [];
            long total = 0;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                CheckAttachmentComponents(path, 403);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                CheckAttachmentComponents(path, 403);
                total += file.Length;
                if (total > DocumentLimit) throw new CrmFault(422, "Pakiet rysunków przekracza limit 100 MiB.");
                var bytes = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(bytes, token);
                if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new CrmFault(422, "Rysunek nie jest poprawnym PDF.");
                drawings.Add(bytes);
            }
            var output = await Task.Run(() => GenerateDocument(data, kind, drawings, token), token);
            var number = OrderDocumentRenderer.SafeFilename(OrderDocumentRenderer.Text(data.Order, "order_number", "id"), orderId.ToString(CultureInfo.InvariantCulture));
            var filename = kind switch { "operations" => $"Arkusze_operacji_{number}.zip", "oferta" => $"Oferta_{number}.pdf", _ => $"Arkusz_{number}.pdf" };
            return new AttachmentDownload(Results.File(output, kind == "operations" ? "application/zip" : "application/pdf", filename));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CrmFault(503, "Generowanie dokumentu trwało zbyt długo. Ponów pobieranie."); }
        catch (CrmFault) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException)
        { throw new CrmFault(422, "Nie można wygenerować kompletnego dokumentu. Sprawdź rysunki i załączniki PDF."); }
        finally { DocumentSlots.Release(); }
    }

    internal static byte[] GenerateDocument(OrderDocumentData data, string kind, IReadOnlyList<byte[]> drawings, CancellationToken ct)
    {
        if (kind != "operations") return OrderDocumentRenderer.Render(data, kind == "oferta", null, drawings, ct);
        var operations = OrderDocumentRenderer.Operations(data);
        if (operations.Length > 200) throw new CrmFault(422, "Pakiet może zawierać maksymalnie 200 operacji.");
        using var output = new MemoryStream();
        long size = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < Math.Max(operations.Length, 1); index++)
            {
                ct.ThrowIfCancellationRequested();
                JsonElement? operation = operations.Length == 0 ? null : operations[index];
                var bytes = OrderDocumentRenderer.Render(data, false, operation, drawings, ct);
                size += bytes.Length;
                if (size > DocumentLimit) throw new CrmFault(422, "Pakiet dokumentów przekracza limit 100 MiB.");
                var order = OrderDocumentRenderer.SafeFilename(OrderDocumentRenderer.Text(data.Order, "order_number", "id"), "order");
                var name = operation is null ? $"Arkusz_{order}.pdf" : $"Arkusz_{order}_{index + 1:00}_{OrderDocumentRenderer.SafeFilename(OrderDocumentRenderer.Text(operation.Value, "name", "op"), $"operacja_{index + 1}")}.pdf";
                using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                entry.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private sealed record DocumentSetting(string Key, string? Value);
    private sealed record DocumentAttachment(string StoredPath);
}

internal sealed record OrderDocumentData(JsonElement Order, JsonElement Template, JsonElement Quote,
    IReadOnlyDictionary<string, string> Company, double Vat, DateOnly PrintedOn);
