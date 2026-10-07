using Rcm.Contracts;

namespace Rcm.Host;

internal static class CatalogKeywords
{
    private static readonly (string Key, string Words)[] Synonyms =
    [
        ("plazma", "blacha s235 s355 hardox cięcie palenie wyciąć"),
        ("cięcie", "cięcie wycięcie dociąć profil blacha"),
        ("piła", "profil rhs shs chs pręt rura cięcie"),
        ("wiercenie", "otwór gwint tuleja frezowanie rozwiercić"),
        ("frezowanie", "otwór gwint tuleja wiercenie"),
        ("spawanie", "mig mag naprawa pęknięcie rama łyżka"),
        ("montaż", "montaż pasowanie składanie komplet"),
        ("malowanie", "malowanie pomalować farba podkład"),
        ("kontrola", "kontrola pomiar jakość wymiary odbiór"),
        ("gięcie", "gięcie zagięcie prasa łuk blacha"),
        ("zagięcie", "gięcie zagięcie prasa łuk blacha")
    ];
    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    public static string Auto(string? name, string? department, string? formula)
    {
        var explicitText = formula?.Trim();
        if (!string.IsNullOrEmpty(explicitText) && explicitText is not ("1" or "1.0")) return explicitText;
        var text = $"{name ?? ""} {department ?? ""}".ToLowerInvariant();
        var words = new HashSet<string>(Words(text.Replace('/', ' ')).Where(w => w.Length >= 3), StringComparer.Ordinal);
        foreach (var (key, extra) in Synonyms) if (text.Contains(key, StringComparison.Ordinal)) words.UnionWith(Words(extra));
        return string.Join(' ', words.Order(StringComparer.Ordinal));
    }
    public static CatalogOperationDto[] Suggest(IEnumerable<CatalogOperationDto> source, string? text, string? material)
    {
        var all = source.OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id).ToArray();
        var haystack = $"{text ?? ""} {material ?? ""}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack)) return all.Take(8).ToArray();
        var tokens = Words(haystack.Replace('/', ' ').Replace(',', ' ')).Where(t => t.Length >= 3).Distinct(StringComparer.Ordinal).ToArray();
        var scored = all.Select(op =>
        {
            var formula = (op.Formula ?? "").ToLowerInvariant();
            var keywords = $"{op.Name} {op.Department ?? ""} {op.Formula ?? ""}".ToLowerInvariant();
            var score = tokens.Sum(t => keywords.Contains(t, StringComparison.Ordinal) ? formula.Contains(t, StringComparison.Ordinal) ? 2 : 1 : 0);
            return (op, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.op.Name, StringComparer.Ordinal).ThenBy(x => x.op.Id).Take(8).Select(x => x.op).ToArray();
        return scored.Length > 0 ? scored : all.Take(6).ToArray();
    }
}
