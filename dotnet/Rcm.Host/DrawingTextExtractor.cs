using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rcm.Contracts;
using Rcm.Crm;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Rcm.Host;

internal static class DrawingTextExtractor
{
    private const int MaxPages = 20;
    private const int MaxBytes = 25 * 1024 * 1024;
    private const int MaxTextCharacters = 2_000_000;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Whitespace = Pattern(@"\s+");
    private static readonly Regex DrawingNumber = Pattern(@"\APK1-\d{5}-\d{3}\z");
    private static readonly Regex FilenameDrawingNumber = Pattern(@"\bPK1-\d{5}-\d{3}\b", true);
    private static readonly Regex Title = Pattern(@"NAZWA CZĘŚCI\s+(.+?)\s+(?:NUMER RYSUNKU|MATERIAŁ:|MASA:|NIE SKALOWAĆ!)", true);
    private static readonly Regex Material = Pattern(@"MATERIAŁ:\s*([^\n|]+?)(?:\s+wg\b|\n|$)", true);
    private static readonly Regex Mass = Pattern(@"MASA:\s*([0-9]+(?:[,.][0-9]+)?)\s*\[?kg\]?", true);
    private static readonly Regex[] Profiles =
    [
        Pattern(@"\b(?:RHS|SHS|CHS)\s+[0-9]+(?:[,.][0-9]+)?x[0-9]+(?:[,.][0-9]+)?x[0-9]+(?:[,.][0-9]+)?\b", true),
        Pattern(@"\bROD\s+[0-9]+(?:[,.][0-9]+)?mm(?:,\s*L=[0-9]+(?:[,.][0-9]+)?mm)?\b", true),
        Pattern(@"\bBL\s+[0-9]+(?:[,.][0-9]+)?mm\b", true)
    ];
    private static readonly Regex Bom = Pattern(@"\A(?<item>\d+)\s+(?<body>.+?)\s+(?<material>(?:\d\.\d{4}\s+\([^)]+\))|(?=\S*[A-Za-z])\S+)\s+(?<qty>\d+(?:[,.]\d+)?)\z");
    private static readonly Regex BomPart = Pattern(@"\A(?<part>PK1-\d{5}-\d{3})\s+(?<description>.+)");

    public static TemplateDrawingPreview Extract(byte[] pdf, string filename, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pdf.Length > MaxBytes) throw new CrmFault(400, "PDF przekracza limit 25 MiB.");
        try
        {
            using var document = PdfDocument.Open(pdf);
            if (document.NumberOfPages > MaxPages)
                throw new CrmFault(400, $"PDF ma {document.NumberOfPages} stron; limit ekstrakcji to {MaxPages}.");
            List<IReadOnlyList<string>> pages = [];
            var length = 0;
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(page);
                length = checked(length + text.Length);
                if (length > MaxTextCharacters) throw new CrmFault(400, "PDF zawiera zbyt dużo tekstu do ekstrakcji.");
                pages.Add(text.Split(['\r', '\n', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries));
            }
            return Parse(pages, filename, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (CrmFault) { throw; }
        catch (Exception)
        {
            throw new CrmFault(422, "Nie udało się odczytać tekstu PDF.");
        }
    }

    internal static TemplateDrawingPreview Parse(IReadOnlyList<IReadOnlyList<string>> pages, string filename, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (pages.Count > MaxPages) throw new CrmFault(400, $"PDF ma {pages.Count} stron; limit ekstrakcji to {MaxPages}.");
        try
        {
            List<IReadOnlyList<string>> cleanedPages = [];
            List<string> lines = [];
            long length = 0;
            foreach (var page in pages)
            {
                List<string> cleaned = [];
                foreach (var line in page)
                {
                    ct.ThrowIfCancellationRequested();
                    length += line.Length;
                    if (length > MaxTextCharacters) throw new CrmFault(400, "PDF zawiera zbyt dużo tekstu do ekstrakcji.");
                    var value = Clean(line);
                    if (value.Length > 0) cleaned.Add(value);
                }
                cleanedPages.Add(cleaned);
                lines.AddRange(cleaned);
            }
            var stem = Path.GetFileNameWithoutExtension(filename.Replace('\\', '/'));
            var number = lines.FirstOrDefault(line => DrawingNumber.IsMatch(line));
            if (number is null)
            {
                var match = FilenameDrawingNumber.Match(stem);
                if (match.Success) number = match.Value.ToUpperInvariant();
            }
            var name = ExtractTitle(lines) ?? number ?? stem;
            var joined = string.Join('\n', lines);
            var material = ExtractMaterial(lines, joined);
            var massMatch = Mass.Match(joined);
            var mass = massMatch.Success ? Float(massMatch.Groups[1].Value) : null;
            var profileText = string.Join(" | ", lines);
            var profile = Profiles.Select(pattern => pattern.Match(profileText)).FirstOrDefault(match => match.Success)?.Value;
            if (profile is not null) profile = Clean(profile);
            var materials = ExtractBom(lines, ct);
            if (materials.Count == 0 && (!string.IsNullOrEmpty(material) || profile is not null || mass is not (null or 0)))
                materials.Add(new()
                {
                    ["line_id"] = "M1", ["part_no"] = number, ["mat"] = string.IsNullOrEmpty(material) ? "Materiał wg rysunku" : material,
                    ["name"] = name, ["dim"] = profile ?? "", ["qty"] = 1, ["unit"] = "szt", ["mass_kg"] = mass,
                    ["source"] = "drawing_title_block", ["requires_manual_check"] = string.IsNullOrEmpty(material)
                });
            var operations = ExtractWeldOperations(cleanedPages, ct);
            if (operations.Count == 0 && profile is not null)
            {
                operations.Add(Preparation("CNC"));
                operations.Add(ProfileOperation("Cięcie profilu wg rysunku", "CNC"));
                operations.Add(ProfileOperation("Kontrola wymiarów wg rysunku", "KJ"));
            }
            return new(number, name, material, mass, profile, pages.Count,
                JsonSerializer.SerializeToElement(materials), JsonSerializer.SerializeToElement(operations), string.Join('\n', lines.Take(80)));
        }
        catch (RegexMatchTimeoutException) { throw new CrmFault(422, "Tekst PDF jest zbyt złożony do ekstrakcji."); }
    }

    private static Regex Pattern(string pattern, bool ignoreCase = false) => new(pattern,
        RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), MatchTimeout);
    private static string Clean(string value) => Whitespace.Replace(value, " ").Trim();
    private static double? Float(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static string? ExtractTitle(IReadOnlyList<string> lines)
    {
        const string title = "NAZWA CZĘŚCI";
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Equals(title, StringComparison.OrdinalIgnoreCase) && i + 1 < lines.Count) return lines[i + 1];
            if (line.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            {
                var inline = Clean(line[title.Length..]);
                if (inline.Length > 0) return inline;
            }
        }
        var match = Title.Match(string.Join(' ', lines));
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static string? ExtractMaterial(IReadOnlyList<string> lines, string joined)
    {
        var match = Material.Match(joined);
        if (match.Success) return Clean(match.Groups[1].Value);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("MATERIAŁ:", StringComparison.Ordinal)) continue;
            var inline = Clean(lines[i]["MATERIAŁ:".Length..]);
            if (inline.Length > 0) return inline;
            if (i + 1 < lines.Count) return lines[i + 1];
        }
        return null;
    }

    private static List<Dictionary<string, object?>> ExtractBom(IReadOnlyList<string> lines, CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows = [];
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var match = Bom.Match(line);
            if (!match.Success) continue;
            var body = Clean(match.Groups["body"].Value);
            var partMatch = BomPart.Match(body);
            var part = partMatch.Success ? partMatch.Groups["part"].Value : null;
            var description = Clean(partMatch.Success ? partMatch.Groups["description"].Value : body);
            var quantity = Float(match.Groups["qty"].Value);
            if (!int.TryParse(match.Groups["item"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var item)) continue;
            rows.Add(new()
            {
                ["line_id"] = $"M{rows.Count + 1}", ["item_no"] = item, ["part_no"] = part,
                ["mat"] = match.Groups["material"].Value, ["name"] = part ?? description, ["dim"] = description,
                ["qty"] = quantity is null or 0 ? 1 : quantity, ["unit"] = "szt", ["source"] = "drawing_bom"
            });
        }
        return rows;
    }

    private static List<Dictionary<string, object?>> ExtractWeldOperations(IReadOnlyList<IReadOnlyList<string>> pages, CancellationToken ct)
    {
        List<Dictionary<string, object?>> operations = [];
        for (var page = 0; page < pages.Count; page++)
        {
            ct.ThrowIfCancellationRequested();
            for (var index = 0; index < pages[page].Count; index++)
            {
                var stage = pages[page][index];
                if (!stage.StartsWith("ETAP ", StringComparison.Ordinal)) continue;
                var title = index > 0 ? pages[page][index - 1] : stage;
                var name = $"{stage}: {title}";
                operations.Add(new()
                {
                    ["op"] = name, ["name"] = name, ["wydział"] = "Spawalnia", ["hours"] = 0, ["rate_per_hour"] = 90,
                    ["material"] = pages[page].Any(line => line.Contains("PN-EN ISO 14341", StringComparison.Ordinal)) ? "PN-EN ISO 14341-A: G3Si1" : "",
                    ["source_page"] = page + 1, ["source"] = "drawing_weld_table"
                });
                break;
            }
        }
        if (operations.Count > 0) operations.Insert(0, Preparation("Spawalnia"));
        return operations;
    }

    private static Dictionary<string, object?> Preparation(string department) => new()
    {
        ["op"] = "Przygotowanie stanowiska / maszyny", ["name"] = "Przygotowanie stanowiska / maszyny", ["wydział"] = department,
        ["hours"] = 0, ["rate_per_hour"] = 0, ["is_preparation"] = true, ["source"] = "system_preparation"
    };

    private static Dictionary<string, object?> ProfileOperation(string name, string department) => new()
    {
        ["op"] = name, ["name"] = name, ["wydział"] = department, ["hours"] = 0,
        ["rate_per_hour"] = 90, ["material_ref"] = "M1", ["source"] = "drawing_profile"
    };
}
