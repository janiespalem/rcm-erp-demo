using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Rcm.Crm;

namespace Rcm.Host;

internal static class OrderDocumentRenderer
{
    private static readonly Lazy<bool> Fonts = new(() => { GlobalFontSettings.FontResolver = new DocumentFontResolver(); return true; });
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    internal static string Text(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
                if (text.Length != 0) return text;
            }
        return "";
    }
    internal static double Number(JsonElement row, string key) => double.TryParse(Text(row, key), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static bool Flag(JsonElement row, string key) => Text(row, key) == "True" || Text(row, key) == "true";
    private static JsonElement[] Array(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    internal static JsonElement[] Operations(OrderDocumentData data)
    {
        var ops = Array(data.Quote, "processes_json");
        return ops.Length != 0 ? ops : Array(data.Template, "operations_json");
    }
    private static string N(double value) => value.ToString("0.###", Polish);
    private static string Money(double value) => Math.Round(value, 2, MidpointRounding.ToEven).ToString("N2", Polish) + " PLN";
    private static string Dash(string value) => value == "" ? "—" : value;
    internal static string SafeFilename(string value, string fallback)
    {
        var safe = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' ? c : '_').ToArray());
        safe = string.Join('_', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('.', '_');
        if (safe.Length > 70) safe = safe[..70];
        return safe.Length == 0 ? fallback : safe;
    }

    internal static byte[] Render(OrderDocumentData data, bool offer, JsonElement? singleOperation,
        IReadOnlyList<byte[]> drawings, CancellationToken ct)
    {
        _ = Fonts.Value;
        using var document = new PdfDocument();
        document.Info.Title = offer ? "Oferta handlowa" : "Arkusz zlecenia";
        document.Info.Author = data.Company["name"];
        using (var canvas = new Canvas(document, data, ct))
        {
            if (offer) Offer(canvas, data); else Worksheet(canvas, data, singleOperation);
        }
        if (!offer)
            foreach (var drawing in drawings)
            {
                ct.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(drawing, writable: false);
                using var imported = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                if (imported.PageCount == 0) throw new CrmFault(422, "Rysunek PDF nie zawiera stron.");
                for (var i = 0; i < imported.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (document.PageCount >= 500) throw new CrmFault(422, "Dokument przekracza limit 500 stron.");
                    document.AddPage(imported.Pages[i]);
                }
            }
        ct.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        document.Save(output, false);
        if (output.Length > 100L * 1024 * 1024) throw new CrmFault(422, "Dokument przekracza limit 100 MiB.");
        return output.ToArray();
    }

    private static void Worksheet(Canvas c, OrderDocumentData d, JsonElement? single)
    {
        var o = d.Order; var t = d.Template; var q = d.Quote;
        c.Title("Arkusz zlecenia", Text(o, "order_number", "id"));
        c.Field("Termin", Deadline(o));
        c.Field("Nr rysunku", Dash(Text(o, "drawing_number") is { Length: > 0 } drawing ? drawing : Text(t, "position_nr")));
        if (Text(o, "dimensions") != "") c.Field("Wymiary", Text(o, "dimensions"));
        if (Number(o, "weight_kg") > 0) c.Field("Masa", N(Number(o, "weight_kg")) + " kg");
        c.Line(Text(o, "order_type") switch { "remont" => "Remont", "nowa_czesc" => "Nowa część", "zbrojenie" => "Zbrojenie", _ => "Produkcja" });
        if (Flag(o, "is_internal")) c.Line("Zlecenie wewnętrzne", bold: true);
        if (Flag(o, "requires_visit")) c.Line("Zlecenie wymaga wizyty u klienta przed realizacją", bold: true);
        c.Section("Legenda zlecenia");
        c.Field("Klient", Text(o, "client"));
        if (Text(t, "name") != "") c.Field("Produkt", Text(t, "name"));
        if (Text(o, "description") != "") c.Field("Opis / cel", Text(o, "description"));
        c.Field("Materiał ogólny", Dash(Text(o, "material")));
        c.Field("Ilość", N(Math.Max(Number(o, "quantity"), 1)) + " szt.");
        if (Number(q, "material_weight_kg") > 0) c.Field("Masa surowca", N(Number(q, "material_weight_kg")) + " kg");
        if (Number(q, "weight_netto_kg") > 0) c.Field("Masa wyrobu", N(Number(q, "weight_netto_kg")) + " kg");
        if (Text(o, "notes") != "") c.Field("Uwagi", Text(o, "notes"));
        var materials = Array(q, "materials_json");
        if (materials.Length == 0) materials = Array(o, "materials_json");
        if (materials.Length == 0) materials = Array(t, "materials_json");
        if (materials.Length > 1000) throw new CrmFault(422, "Lista materiałów przekracza limit 1000 pozycji.");
        if (materials.Length != 0)
        {
            c.Section("Lista materiałów");
            c.Row(["#", "Nr części", "Materiał", "Wymiary / opis", "Ilość", "JM", "Masa kg"], [22, 85, 90, 150, 45, 30, 53], header: true);
            for (var i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                var name = m.ValueKind == JsonValueKind.String ? m.GetString()! : Text(m, "name");
                c.Row([(i + 1).ToString(), Dash(Text(m, "part_no", "name", "line_id")), Dash(Text(m, "mat", "material", "name") is { Length: > 0 } mat ? mat : name),
                    Dash(Text(m, "dim", "name")), Text(m, "qty", "qty_kg") is { Length: > 0 } qty && qty != "0" ? qty : "1",
                    Text(m, "unit") is { Length: > 0 } unit ? unit : "szt", Dash(Text(m, "mass_kg", "qty_kg"))], [22, 85, 90, 150, 45, 30, 53]);
            }
        }
        else if (Text(t, "drawing_path") != "")
        {
            c.Section("Lista materiałów");
            c.Line("Lista materiałów wbudowana w rysunek techniczny — patrz strony dołączone do zlecenia (nr " + Text(t, "position_nr", "name") + ").");
        }
        var ops = single is null ? Operations(d) : [single.Value];
        if (ops.Length > 200) throw new CrmFault(422, "Dokument przekracza limit 200 operacji.");
        var routing = ops.Select(op => Text(op, "wydział", "department")).Where(s => s != "").Distinct().ToArray();
        if (routing.Length != 0) { c.Section("Routing wydziałów"); c.Line(string.Join(" → ", routing)); }
        var instructions = Array(t, "instruction_blocks");
        if (instructions.Length > 200) throw new CrmFault(422, "Dokument przekracza limit 200 instrukcji.");
        if (instructions.Length != 0)
        {
            c.Section("Instrukcje technologiczne");
            foreach (var instruction in instructions.OrderBy(i => Number(i, "order"))) c.Line(Text(instruction, "text"));
        }
        c.Section("Operacje · " + ops.Length + " czynności · " + N(ops.Sum(op => Number(op, "hours"))) + " h łącznie");
        for (var i = 0; i < Math.Max(ops.Length, 6 * (ops.Length == 0 ? 1 : 0)); i++)
        {
            var op = ops.Length == 0 ? default : ops[i];
            var material = Text(op, "material");
            if (material != "" && Text(op, "dim") != "") material += " · " + Text(op, "dim");
            if (material == "") material = Text(op, "material_ref");
            if (material == "" && i == 0) material = Text(o, "material");
            c.Row(["#", "Czynność", "Wydział", "Materiał / Wymiary", "Plan h", "Real. czas", "Podpis"], [22, 145, 70, 133, 35, 50, 20], header: true);
            c.Row([(i + 1).ToString(), Dash(Text(op, "name", "op")), Text(op, "wydział", "department"), Dash(material),
                Flag(op, "is_preparation") || Number(op, "hours") <= 0 ? "—" : N(Number(op, "hours")), "________", "___"], [22, 145, 70, 133, 35, 50, 20], minimumHeight: 30);
            if (Text(op, "source_page") != "") c.Line("Rysunek: str. " + Text(op, "source_page"), size: 8);
            if (ops.Length != 0)
            {
                c.Row(["Data", "Pracownik", "Wykonana czynność / uwagi", "Czas", "Podpis"], [65, 95, 220, 50, 45], header: true);
                for (var j = 0; j < 3; j++) c.Row(["________", "____________", "_____________________________", "_____", "_____"], [65, 95, 220, 50, 45], minimumHeight: 22);
            }
        }
        c.Section("Uwagi (odręcznie)");
        for (var i = 0; i < 4; i++) c.Line("_________________________________________________________________________");
        c.Section("Podpis końcowy");
        c.Row(["Wykonał", "Sprawdził", "Jakość OK / Gotowe do wydania"], [158, 158, 159], header: true);
        c.Row(["___________________\nData: ___________", "___________________\nData: ___________", "[  ] Jakość OK\n[  ] Gotowe do wydania"], [158, 158, 159], minimumHeight: 42);
    }

    private static void Offer(Canvas c, OrderDocumentData d)
    {
        var o = d.Order; var t = d.Template; var q = d.Quote;
        var quoted = q.ValueKind == JsonValueKind.Object;
        c.Title("Oferta handlowa", $"OF/{Text(o, "order_number", "id")}/{d.PrintedOn.Year}");
        c.Line(d.Company["tagline"]);
        c.Line(d.Company["address"]);
        c.Line("NIP: " + d.Company["nip"] + " | REGON: " + d.Company["regon"]);
        if (Flag(o, "is_defence")) c.Line("PROJEKT ZBROJENIOWY / MON — DOKUMENT POUFNY", bold: true);
        c.Section("Oferta dla: " + Text(o, "client"));
        c.Field("Termin realizacji", Deadline(o));
        c.Field("Zlecenie", Text(o, "order_number", "id"));
        var quantity = Math.Max(Number(o, "quantity"), 1);
        c.Field("Ilość", N(quantity) + " szt.");
        if (Text(o, "delivery_address") != "") c.Field("Adres dostawy", Text(o, "delivery_address"));
        if (Text(o, "contact") != "") c.Field("Kontakt", Text(o, "contact"));
        if (Text(o, "description") != "") { c.Section("Zakres prac / Opis"); c.Line(Text(o, "description")); }
        var subject = Text(t, "name") is { Length: > 0 } name ? name : Text(o, "sop_name");
        if (subject == "") subject = quoted ? Text(o, "order_type") switch
            { "nowa_czesc" => "Projekt indywidualny / Nowa część", "remont" => "Usługa remontowa / Naprawa", _ => "Usługa według specyfikacji" } : "Usługa";
        double? net = quoted ? Number(q, "total_net") : Number(t, "base_price_pln") > 0
            ? Math.Round(Number(t, "base_price_pln") * (1 + Number(t, "margin_pct")) * quantity, 2, MidpointRounding.ToEven) : null;
        c.Section("Przedmiot / Usługa");
        c.Row(["#", "Przedmiot / Usługa", "Ilość", "Cena netto"], [25, 305, 60, 85], header: true);
        c.Row(["1", subject, N(quantity) + " szt.", net is null ? "Do ustalenia" : Money(net.Value)], [25, 305, 60, 85]);
        if (quoted && !Flag(o, "is_defence") && Flag(q, "show_unit_prices"))
        {
            var ops = Array(q, "processes_json");
            if (ops.Length > 200) throw new CrmFault(422, "Oferta przekracza limit 200 operacji.");
            foreach (var op in ops)
                c.Line(Text(op, "name", "op") + (Number(op, "hours") > 0 && Number(op, "rate_per_hour") > 0
                    ? $" — {N(Number(op, "hours"))} h × {N(Number(op, "rate_per_hour"))} PLN/h" : ""));
        }
        c.Section("Podsumowanie ceny");
        var vatPercent = Math.Round(d.Vat * 100, MidpointRounding.ToEven);
        if (net is { } amount)
        {
            c.Field("Wartość netto", Money(amount));
            c.Field($"VAT ({N(vatPercent)}%)", Money(amount * d.Vat));
            c.Field("Razem brutto", Money(amount * (1 + d.Vat)));
        }
        else c.Line("Wycena indywidualna", bold: true);
        c.Section("Warunki oferty");
        c.Line("Oferta ważna 14 dni od daty wystawienia.");
        c.Line($"Ceny netto, do których należy doliczyć podatek VAT {N(vatPercent)}%.");
        c.Line("Realizacja po wpłacie zaliczki 50% lub po podpisaniu umowy.");
        c.Line("Płatność przelewem — termin 14 dni od daty FV.");
        c.Line("Koszty transportu wyceniane osobno.");
        c.Section("Podpisy");
        c.Row(["Wystawił — Biuro FactoryFlow", "Przyjął — Klient"], [237, 238], header: true);
        c.Row([$"________________________\nData: {d.PrintedOn:dd.MM.yyyy}", "________________________\nData: ____________"], [237, 238], minimumHeight: 42);
    }
    private static string Deadline(JsonElement o) => DateOnly.TryParse(Text(o, "deadline"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("dd.MM.yyyy") : "—";

    private sealed class Canvas : IDisposable
    {
        private const double Left = 40, Width = 515, Bottom = 786;
        private readonly PdfDocument document;
        private readonly OrderDocumentData data;
        private readonly CancellationToken ct;
        private XGraphics graphics = null!;
        private double y;
        private int section;
        private readonly XBrush ink = new XSolidBrush(XColor.FromArgb(26, 58, 92));
        internal Canvas(PdfDocument document, OrderDocumentData data, CancellationToken ct)
        { this.document = document; this.data = data; this.ct = ct; NewPage(); }
        private static XFont Font(double size, bool bold = false) => new("RCM Sans", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        private void NewPage()
        {
            ct.ThrowIfCancellationRequested();
            if (document.PageCount >= 500) throw new CrmFault(422, "Dokument przekracza limit 500 stron.");
            graphics?.Dispose();
            var page = document.AddPage(); page.Size = PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            y = 40;
            graphics.DrawString(data.Company["name"], Font(19, true), ink, new XRect(Left, y, Width, 24), XStringFormats.TopLeft);
            graphics.DrawString($"Wydruk: {data.PrintedOn:dd.MM.yyyy} · {document.PageCount}", Font(8), XBrushes.Gray, new XRect(Left, y, Width, 20), XStringFormats.TopRight);
            y += 30;
            graphics.DrawLine(new XPen(XColor.FromArgb(26, 58, 92), 2), Left, y, Left + Width, y); y += 10;
            graphics.DrawString(data.Company["name"] + " Sp. z o.o. · " + data.Company["address"], Font(7), XBrushes.Gray,
                new XRect(Left, Bottom + 16, Width, 15), XStringFormats.TopLeft);
        }
        private void Ensure(double height) { ct.ThrowIfCancellationRequested(); if (y + height > Bottom) NewPage(); }
        internal void Title(string title, string number) { Line(title, 17, true); Line(number, 12, true); }
        internal void Field(string label, string value) => Line(label + ": " + value);
        internal void Section(string title)
        {
            var font = Font(10, true); var lines = Wrap(++section + ". " + title, font, Width - 12);
            var height = lines.Count * 14 + 8; Ensure(height + 22);
            graphics.DrawRectangle(ink, Left, y, Width, height);
            foreach (var line in lines) { graphics.DrawString(line, font, XBrushes.White, new XRect(Left + 6, y + 4, Width - 12, 14), XStringFormats.TopLeft); y += 14; }
            y += 12;
        }
        internal void Line(string text, double size = 10, bool bold = false)
        {
            var font = Font(size, bold);
            foreach (var line in Wrap(text, font, Width))
            {
                Ensure(size + 6);
                graphics.DrawString(line, font, ink, new XRect(Left, y, Width, size + 4), XStringFormats.TopLeft);
                y += size + 5;
            }
            y += 2;
        }
        internal void Row(string[] cells, double[] widths, bool header = false, double minimumHeight = 20)
        {
            var font = Font(header ? 7.5 : 8, header);
            var lines = cells.Select((cell, i) => Wrap(cell, font, widths[i] - 8)).ToArray();
            var height = Math.Max(minimumHeight, lines.Max(l => l.Count) * 12 + 8);
            if (height > Bottom - 80)
            {
                for (var offset = 0; offset < lines.Max(l => l.Count); offset += 40)
                    Row(lines.Select(l => string.Join('\n', l.Skip(offset).Take(40))).ToArray(), widths, header, minimumHeight);
                return;
            }
            Ensure(height);
            var x = Left;
            for (var i = 0; i < cells.Length; i++)
            {
                if (header) graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 243, 247)), x, y, widths[i], height);
                graphics.DrawRectangle(new XPen(XColor.FromArgb(220, 225, 230), .5), x, y, widths[i], height);
                for (var j = 0; j < lines[i].Count; j++) graphics.DrawString(lines[i][j], font, ink, new XRect(x + 4, y + 4 + j * 12, widths[i] - 8, 12), XStringFormats.TopLeft);
                x += widths[i];
            }
            y += height;
        }
        private List<string> Wrap(string text, XFont font, double width)
        {
            if (text.Length > 100_000) throw new CrmFault(422, "Pole dokumentu przekracza limit 100 000 znaków.");
            var result = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = new StringBuilder();
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    ct.ThrowIfCancellationRequested();
                    if (line.Length != 0 && graphics.MeasureString(line + " " + word, font).Width > width) { result.Add(line.ToString()); line.Clear(); }
                    if (line.Length != 0) line.Append(' ');
                    foreach (var ch in word)
                    {
                        if (line.Length != 0 && graphics.MeasureString(line.ToString() + ch, font).Width > width) { result.Add(line.ToString()); line.Clear(); }
                        line.Append(ch);
                    }
                }
                result.Add(line.ToString());
            }
            return result;
        }
        public void Dispose() => graphics.Dispose();
    }

    private sealed class DocumentFontResolver : IFontResolver
    {
        private static readonly Lazy<byte[]> Regular = new(() => Read("DejaVuSans.ttf"));
        private static readonly Lazy<byte[]> Bold = new(() => Read("DejaVuSans-Bold.ttf"));
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "rcm-bold" : "rcm-regular");
        public byte[] GetFont(string faceName) => faceName == "rcm-bold" ? Bold.Value : Regular.Value;
        private static byte[] Read(string filename)
        {
            using var stream = typeof(OrderDocumentRenderer).Assembly.GetManifestResourceStream("Rcm.Host.DocumentAssets." + filename)
                ?? throw new InvalidOperationException("Brak czcionki dokumentów.");
            using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
        }
    }
}
