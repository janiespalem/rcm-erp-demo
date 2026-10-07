using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rcm.Desktop;

public abstract class TemplateJsonRow(JsonNode? original, bool fromLegacy) : ObservableObject
{
    protected JsonNode? Original { get; } = original?.DeepClone();
    protected bool FromLegacy { get; } = fromLegacy;
    protected string Read(string fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (Original is JsonObject item && item[key] is { } value) return value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        return fallback;
    }
    protected JsonObject Object() => Original is JsonObject item ? (JsonObject)item.DeepClone() : new();
    protected void Set(JsonObject item, string key, JsonNode? value, params string[] aliases)
    {
        item[key] = value;
        foreach (var alias in aliases) if (item.ContainsKey(alias)) item[alias] = value?.DeepClone();
    }
    protected static JsonNode? Number(string value, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return null;
        if (!OrderEditorViewModel.TryNumber(value, out var number) || number < 0 || number > 1_000_000)
            throw new ArgumentException("Podaj liczbę od 0 do 1 000 000.");
        return JsonValue.Create(number);
    }
    public bool IsEditable => !FromLegacy || Original is JsonObject || Original is JsonValue value && value.TryGetValue<string>(out _);
    public abstract string Snapshot { get; }
    public abstract JsonNode? Export();
    protected static string State(params string[] values) => JsonSerializer.Serialize(values);
}

public sealed partial class TemplateOperationRow : TemplateJsonRow
{
    private readonly string initial;
    [ObservableProperty] private string name;
    [ObservableProperty] private string department;
    [ObservableProperty] private string hours;
    [ObservableProperty] private string rate;
    public TemplateOperationRow(JsonNode? node = null, double laborRate = 90, bool fromLegacy = false) : base(node, fromLegacy)
    {
        name = Read(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "", "op", "name", "wydział");
        department = Read("", "wydział", "department"); hours = Read("0", "hours");
        rate = Read(laborRate.ToString(CultureInfo.CurrentCulture), "rate_per_hour", "rate"); initial = Snapshot;
    }
    public override string Snapshot => State(Name, Department, Hours, Rate);
    public override JsonNode? Export()
    {
        if (FromLegacy && Snapshot == initial) return Original?.DeepClone();
        if (!IsEditable) return Original?.DeepClone();
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Wpisz nazwę operacji.");
        var row = Object(); Set(row, "op", JsonValue.Create(Name.Trim()), "name"); Set(row, "wydział", JsonValue.Create(Department.Trim()), "department");
        row["hours"] = Number(Hours); Set(row, "rate_per_hour", Number(Rate), "rate"); return row;
    }
}

public sealed partial class TemplateMaterialRow : TemplateJsonRow
{
    private readonly string initial;
    [ObservableProperty] private string name;
    [ObservableProperty] private string dimension;
    [ObservableProperty] private string quantity;
    [ObservableProperty] private string unit;
    [ObservableProperty] private string mass;
    public TemplateMaterialRow(JsonNode? node = null, bool fromLegacy = false) : base(node, fromLegacy)
    {
        name = Read(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "", "mat", "name");
        dimension = Read("", "dim", "dimensions"); quantity = Read("1", "qty", "qty_kg"); unit = Read("szt", "unit"); mass = Read("", "mass_kg"); initial = Snapshot;
    }
    public override string Snapshot => State(Name, Dimension, Quantity, Unit, Mass);
    public override JsonNode? Export()
    {
        if (FromLegacy && Snapshot == initial) return Original?.DeepClone();
        if (!IsEditable) return Original?.DeepClone();
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Wpisz nazwę materiału.");
        var row = Object(); Set(row, "mat", JsonValue.Create(Name.Trim()), "name"); Set(row, "dim", JsonValue.Create(Dimension.Trim()), "dimensions");
        Set(row, "qty", Number(Quantity), "qty_kg"); row["unit"] = Unit.Trim(); row["mass_kg"] = Number(Mass, true); return row;
    }
}

public sealed partial class TemplateInstructionRow : TemplateJsonRow
{
    private readonly string initial;
    [ObservableProperty] private string text;
    [ObservableProperty] private string sequence;
    public TemplateInstructionRow(JsonNode? node = null, int index = 1, bool fromLegacy = false) : base(node, fromLegacy)
    {
        text = Read(node is JsonValue value && value.TryGetValue<string>(out var valueText) ? valueText : "", "text", "instruction");
        sequence = Read(index.ToString(), "order"); initial = Snapshot;
    }
    public override string Snapshot => State(Text, Sequence);
    public override JsonNode? Export()
    {
        if (FromLegacy && Snapshot == initial) return Original?.DeepClone();
        if (!IsEditable) return Original?.DeepClone();
        if (string.IsNullOrWhiteSpace(Text)) throw new ArgumentException("Wpisz instrukcję SOP.");
        if (Original is JsonValue) return JsonValue.Create(Text.Trim());
        var row = Object(); Set(row, "text", JsonValue.Create(Text.Trim()), "instruction"); row["order"] = Number(Sequence); return row;
    }
}

public sealed partial class TemplateMachineRow : TemplateJsonRow
{
    private readonly string initial;
    [ObservableProperty] private string name;
    public TemplateMachineRow(JsonNode? node = null, bool fromLegacy = false) : base(node, fromLegacy)
    { name = Read(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "", "name", "machine", "maszyna"); initial = Snapshot; }
    public override string Snapshot => State(Name);
    public override JsonNode? Export()
    {
        if (FromLegacy && Snapshot == initial) return Original?.DeepClone();
        if (!IsEditable) return Original?.DeepClone();
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Wpisz nazwę maszyny.");
        if (Original is JsonValue || Original is null) return JsonValue.Create(Name.Trim());
        var row = Object(); Set(row, "name", JsonValue.Create(Name.Trim()), "machine", "maszyna"); return row;
    }
}
