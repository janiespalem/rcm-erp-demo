using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class ShiftReportsField(string key, string label, int limit = 100, bool number = false, bool multiline = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public int Limit { get; } = limit;
    public bool Number { get; } = number;
    public bool Multiline { get; } = multiline;
    [ObservableProperty] private string value = "";
    [ObservableProperty] private string error = "";
}
public sealed partial class ShiftReportsEquipment(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public static string[] Conditions => ["", "sprawne", "niesprawne"];
    [ObservableProperty] private string condition = "";
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private string error = "";
    public ShiftReportEquipment Export() => new() { Condition = string.IsNullOrEmpty(Condition) ? null : Condition, Reason = Reason.Trim(), Note = Note.Trim() };
}
public sealed partial class ShiftReportsCheck(int index, ShiftReportQuestion question) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = $"{index + 1}. {question.Label}";
    public static string[] Answers => ["", "OK", "NIE", "N/D"];
    [ObservableProperty] private string answer = "";
    [ObservableProperty] private string error = "";
}
public sealed record ShiftReportsChange(string Label, string Before, string After);
public sealed record ShiftReportsAuditRow(ShiftReportAuditDto Entry, ShiftReportsChange[] Changes)
{
    public string Heading => $"{Entry.CreatedAt.LocalDateTime:g} · {Entry.ActorName} · {Entry.Action switch { "created" => "Utworzono", "saved" => "Zapis szkicu", "finalized" => "Zakończono", "corrected" => "Korekta", "deleted" => "Usunięto", _ => Entry.Action }}";
    public string Reason => Entry.Reason ?? "";
}
public sealed record ShiftReportsListRow(ShiftReportDto Report)
{
    public string Date => Report.ReportDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public string Shift => Report.Shift;
    public string Author => Report.AuthorName;
    public string State => StateName(Report.Status);
    public string DailyLabel => $"Zmiana {Shift} · {State}";
    public string Quantity => $"{Report.Fields.Poured?.ToString() ?? "—"} / {Report.Fields.Damaged?.ToString() ?? "—"}";
    public string Warning => Report.DeletedAt is not null ? "Usunięty" : Report.Warning ? "Wymaga uwagi" : "—";
    public static string StateName(string state) => state switch { "draft" => "Szkic", "finalized" => "Zakończony", "corrected" => "Skorygowany", _ => state };
}
public static class ShiftReportsDifferences
{
    private static string Key(string value) => value.Replace("_", "").ToLowerInvariant();
    private static Dictionary<string, JsonElement> Flatten(JsonElement? snapshot)
    {
        Dictionary<string, JsonElement> result = [];
        if (snapshot is not { ValueKind: JsonValueKind.Object } item) return result;
        if (item.TryGetProperty("fields", out var fields)) item = fields;
        if (item.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in item.EnumerateObject())
        {
            var key = Key(property.Name);
            if (key == "checks" && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var entry in property.Value.EnumerateArray().Select((value, index) => (value, index))) result[$"checks.{entry.index}"] = entry.value;
            else if (key is "vibrators" or "extensions" && property.Value.ValueKind == JsonValueKind.Object)
                foreach (var entry in property.Value.EnumerateObject()) result[$"{key}.{Key(entry.Name)}"] = entry.Value;
            else result[key] = property.Value;
        }
        return result;
    }
    public static ShiftReportsChange[] Compare(JsonElement? before, JsonElement after, IReadOnlyDictionary<string, string> labels)
    {
        var a = Flatten(before); var b = Flatten(after); List<ShiftReportsChange> changes = [];
        static string Display(JsonElement? value) => value is null || value.Value.ValueKind == JsonValueKind.Null ? "—" : value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "—" : value.Value.ToString();
        foreach (var key in a.Keys.Union(b.Keys))
        {
            JsonElement? old = a.TryGetValue(key, out var x) ? x : null; JsonElement? next = b.TryGetValue(key, out var y) ? y : null;
            if (old?.GetRawText() == next?.GetRawText()) continue;
            changes.Add(new(labels.TryGetValue(key, out var label) ? label : key, Display(old), Display(next)));
        }
        return changes.ToArray();
    }
}

public static class ShiftReportsChoices
{
    public static string[] Shifts => ["I", "II"];
    public static string[] Filters => ["", "I", "II"];
}
