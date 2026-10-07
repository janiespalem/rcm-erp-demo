using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rcm.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LinkProductionReport(Guid RequestId, long ExpectedReportVersion, long ExpectedLinkVersion, Guid ContractId, string Reason = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewProductionReport(Guid RequestId, long ExpectedReportVersion, long ExpectedLinkVersion, string Decision, string Reason = "");
public sealed record ProductionReportLinkDto(long ReportId, long Version, Guid ContractId, string ContractName,
    long SourceVersionAtLink, long LinkedBy, DateTimeOffset LinkedAt);
public sealed record ProductionReportReviewDto(Guid Id, long ReportId, long ReportVersion, long LinkVersion, Guid ContractId,
    long ReviewerId, string Decision, string Reason, DateTimeOffset RecordedAt);
public sealed record ProductionReportReviewState(ShiftReportDto Report, ProductionReportLinkDto? Link,
    ProductionReportReviewDto? CurrentReview, string State);
public sealed record ProductionReviewFeatures(bool Read, bool Link, bool Review);
public sealed record ProductionAcceptedOperations(Guid ContractId, long Reports, long Assembled, long Prepared,
    long Poured, long Checked, long Demoulded, long Damaged);
public sealed record ProductionReportAuditDto(Guid Id, long ReportId, long ActorId, string Action, string Reason,
    JsonElement Changes, DateTimeOffset RecordedAt);
