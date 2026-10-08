using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeTemplates
{
    private static readonly SemaphoreSlim DocumentSlots = new(2);
    public Task<IResult> Worksheet(LegacyUser actor, long id, CancellationToken ct)
    {
        RequireTech(actor);
        return Bounded(DocumentSlots, async token =>
        {
            JsonElement template;
            await using (var db = await Open(token))
                template = (await Read<JsonElement>(db, "SELECT row_to_json(t) FROM public.product_templates t WHERE id=@id", token, P("id", id))).SingleOrDefault();
            if (template.ValueKind != JsonValueKind.Object) throw new CrmFault(404, "Szablon nie znaleziony.");
            var bytes = await Render(template, null, token);
            var name = OrderDocumentRenderer.SafeFilename(OrderDocumentRenderer.Text(template, "position_nr", "id"), id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return (IResult)new TemplateDownload(Results.File(bytes, "application/pdf", name + "_arkusz.pdf"));
        }, ct);
    }
    public Task<IResult> ProjectWorksheets(LegacyUser actor, string code, CancellationToken ct)
    {
        RequireTech(actor);
        if (string.IsNullOrEmpty(code) || code.Length > 50) throw CrmFault.Invalid("projectCode", "Podaj kod projektu (do 50 znaków).");
        return Bounded(DocumentSlots, async token =>
        {
            JsonElement[] templates;
            await using (var db = await Open(token))
                templates = await Read<JsonElement>(db, "SELECT row_to_json(t) FROM public.product_templates t WHERE project_code=@code AND is_active=true ORDER BY position_nr,id LIMIT 201", token, P("code", code));
            if (templates.Length == 0) throw new CrmFault(404, "Projekt nie znaleziony lub brak pozycji.");
            if (templates.Length > 200) throw new CrmFault(422, "Projekt przekracza limit 200 pozycji.");
            using var result = new PdfDocument(); long total = 0;
            foreach (var template in templates)
            {
                token.ThrowIfCancellationRequested(); var pdf = await Render(template, code, token); total += pdf.Length;
                if (total > 100L * 1024 * 1024) throw new CrmFault(422, "Pakiet przekracza limit 100 MiB.");
                using var source = new MemoryStream(pdf, writable: false); using var document = PdfReader.Open(source, PdfDocumentOpenMode.Import);
                for (var page = 0; page < document.PageCount; page++)
                {
                    token.ThrowIfCancellationRequested();
                    if (result.PageCount >= 500) throw new CrmFault(422, "Pakiet przekracza limit 500 stron.");
                    result.AddPage(document.Pages[page]);
                }
            }
            using var output = new MemoryStream(); result.Save(output, false);
            if (output.Length > 100L * 1024 * 1024) throw new CrmFault(422, "Pakiet przekracza limit 100 MiB.");
            return (IResult)new TemplateDownload(Results.File(output.ToArray(), "application/pdf", OrderDocumentRenderer.SafeFilename(code, "projekt") + "_arkusze.pdf"));
        }, ct);
    }
    private async Task<byte[]> Render(JsonElement template, string? project, CancellationToken ct)
    {
        try
        {
            var id = template.GetProperty("id").GetInt64(); var position = OrderDocumentRenderer.Text(template, "position_nr", "id");
            var order = JsonSerializer.SerializeToElement(new
            {
                id, order_number = project is null ? position : project + "/" + position,
                client = project ?? OrderDocumentRenderer.Text(template, "project_code"), description = OrderDocumentRenderer.Text(template, "name"),
                sop_name = OrderDocumentRenderer.Text(template, "position_nr"), quantity = 1, order_type = "catalog",
                notes = project is null ? OrderDocumentRenderer.Text(template, "notes") : null, requires_visit = false
            });
            var quote = JsonSerializer.SerializeToElement(new { processes_json = template.GetProperty("operations_json") });
            var data = new OrderDocumentData(order, template, quote, new Dictionary<string, string> { ["name"] = "FactoryFlow Demo", ["address"] = "Synthetic Avenue 1, Demo City" }, .23,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Europe/Warsaw").DateTime));
            List<byte[]> drawings = [];
            if (OrderDocumentRenderer.Text(template, "drawing_path") != "")
            {
                // Use the snapshot path so a concurrent replacement cannot mix versions in a project packet.
                var path = AttachmentPath(OrderDocumentRenderer.Text(template, "drawing_path"));
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                CheckComponents(path); drawings.Add(await ReadPdf(input, input.Length, ct));
            }
            return await Task.Run(() => OrderDocumentRenderer.Render(data, false, null, drawings, ct), ct);
        }
        catch (CrmFault) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PdfReaderException)
        { throw new CrmFault(422, "Nie można wygenerować kompletnego arkusza. Sprawdź rysunek PDF."); }
    }
}
