using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rcm.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShiftReportEquipment
{
    public string? Condition { get; init; }
    public string Reason { get; init; } = "";
    public string Note { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ShiftReportFields
{
    public string Leader { get; init; } = "";
    public string Responsible { get; init; } = "";
    public int? People { get; init; }
    public string Reference { get; init; } = "";
    public int? Assembled { get; init; }
    public int? Prepared { get; init; }
    public int? Poured { get; init; }
    public int? Checked { get; init; }
    public int? Demoulded { get; init; }
    public int? Damaged { get; init; }
    public string DamageReason { get; init; } = "";
    public ShiftReportEquipment Vibrators { get; init; } = new();
    public ShiftReportEquipment Extensions { get; init; } = new();
    public string?[] Checks { get; init; } = new string?[9];
    public string CorrectedWork { get; init; } = "";
    public string RemainingWork { get; init; } = "";
    public string WorkOwner { get; init; } = "";
    public string Remarks { get; init; } = "";
    public string ProductionPerson { get; init; } = "";
    public string Controller { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateShiftReport(Guid RequestId, DateOnly ReportDate, string Shift);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveShiftReport(Guid RequestId, long ExpectedVersion, ShiftReportFields Fields);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShiftReportVersionCommand(Guid RequestId, long ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CorrectShiftReport(Guid RequestId, long ExpectedVersion, ShiftReportFields Fields, string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminDeleteShiftReport(Guid RequestId, long ExpectedVersion, string Reason);
public sealed record ShiftReportMutationResult(long Id, long Version, bool Deleted);
public sealed record ShiftReportDto(long Id, DateOnly ReportDate, string Shift, long AuthorId, string AuthorName,
    string Status, long Version, int SchemaVersion, ShiftReportFields Fields, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? FinalizedAt, long? FinalizedById, string? FinalizedByName,
    int CorrectionCount, DateTimeOffset? DeletedAt, bool Warning, Dictionary<string, string> ValidationErrors);
public sealed record ShiftReportAuditDto(long Id, long ActorId, string ActorName, string Action,
    string? Reason, JsonElement? Before, JsonElement After, DateTimeOffset CreatedAt);
public sealed record ShiftReportQuestion(string Id, string Label);
public sealed record ShiftReportFeatures(bool Read, bool Write, bool CanAdminDelete,
    IReadOnlyDictionary<int, IReadOnlyList<ShiftReportQuestion>> Schemas);
public sealed record ShiftReportTotals(long Reports, long Assembled, long Prepared, long Poured,
    long Checked, long Demoulded, long Damaged);
public sealed record ShiftReportToday(DateOnly Date, bool CanAdminDelete, ShiftReportDto[] Reports, ShiftReportTotals Totals);
