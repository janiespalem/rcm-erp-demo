using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Rcm.Crm;

namespace Rcm.Host;

internal static class InsightSpreadsheet
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal static readonly string[] Headers = ["Nr", "Klient", "Status", "Gałąź", "Termin", "Wartość netto (PLN)", "Utworzono"];
    internal static readonly int[] Widths = [12, 25, 15, 12, 12, 20, 12];
    internal sealed record Row(string? Number, string Client, string Status, string? Branch, string? Deadline, double Value, string? Created);
    internal static async Task<byte[]> Build(Func<XmlWriter, Task> rows, CancellationToken ct)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string content)
            { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)); writer.Write(content); }
            Part("[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Part("_rels/.rels", """
                <?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Part("xl/workbook.xml", """
                <?xml version="1.0" encoding="utf-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="FactoryFlow orders" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Part("xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Part("xl/styles.xml", """
                <?xml version="1.0" encoding="utf-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1A3A5C"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                """);
            using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
            xml.WriteStartDocument(); xml.WriteStartElement("worksheet", Main); xml.WriteStartElement("cols", Main);
            for (var column = 0; column < 7; column++)
            { xml.WriteStartElement("col", Main); xml.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("width", Widths[column].ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); }
            xml.WriteEndElement(); xml.WriteStartElement("sheetData", Main); xml.WriteStartElement("row", Main); xml.WriteAttributeString("r", "1");
            for (var column = 0; column < 7; column++) Text(xml, column, 1, Headers[column], true);
            xml.WriteEndElement(); await rows(xml); ct.ThrowIfCancellationRequested(); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndDocument(); xml.Flush();
        }
        if (output.Length > 64L * 1024 * 1024) throw new CrmFault(422, "Eksport przekracza limit 64 MiB.");
        return output.ToArray();
    }
    internal static void WriteRow(XmlWriter xml, int index, Row row)
    {
        xml.WriteStartElement("row", Main); xml.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
        Text(xml, 0, index, row.Number); Text(xml, 1, index, row.Client); Text(xml, 2, index, row.Status); Text(xml, 3, index, row.Branch); Text(xml, 4, index, row.Deadline);
        xml.WriteStartElement("c", Main); xml.WriteAttributeString("r", "F" + index.ToString(CultureInfo.InvariantCulture)); xml.WriteElementString("v", Main, row.Value.ToString("R", CultureInfo.InvariantCulture)); xml.WriteEndElement();
        Text(xml, 6, index, row.Created); xml.WriteEndElement();
    }
    private static void Text(XmlWriter xml, int column, int row, string? value, bool header = false)
    {
        xml.WriteStartElement("c", Main); xml.WriteAttributeString("r", ((char)('A' + column)).ToString() + row.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("t", "inlineStr");
        if (header) xml.WriteAttributeString("s", "1");
        xml.WriteStartElement("is", Main); xml.WriteStartElement("t", Main); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        xml.WriteString(string.Concat((value ?? "").EnumerateRunes().Where(r => r.Value is 9 or 10 or 13 || r.Value is >= 32 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF).Select(r => r.ToString())));
        xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
    }
}
