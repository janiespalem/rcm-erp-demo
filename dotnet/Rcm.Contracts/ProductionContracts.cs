using System.Text.Json.Serialization;

namespace Rcm.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SteelMasses(long? Diameter6 = null, long? Diameter12 = null, long? Diameter16 = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionContractFields(string Name, string Reference = "", int? PlannedTetrapods = null,
    DateOnly? Deadline = null, DateOnly? OpeningDate = null, SteelMasses? OpeningSteel = null, bool NormConfirmed = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProductionContract(Guid RequestId, ProductionContractFields Fields, bool IsSynthetic = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveProductionContract(Guid RequestId, long ExpectedVersion, ProductionContractFields Fields, string Reason = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SteelDeliveryFields(DateOnly DeliveryDate, SteelMasses Steel, string Note = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSteelDelivery(Guid RequestId, long ExpectedContractVersion, SteelDeliveryFields Fields);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CorrectSteelDelivery(Guid RequestId, long ExpectedContractVersion, long ExpectedVersion, SteelDeliveryFields Fields, string Reason);

public sealed record ProductionNorm(int BasketsPerTetrapod, SteelMasses GramsPerTetrapod);
public sealed record ProductionCoverage(SteelMasses AccountedSteel, long? PotentialTetrapods, long? PotentialBaskets,
    int[] LimitingDiameters, SteelMasses MissingForPlan);
public sealed record ProductionContractDto(Guid Id, long Version, ProductionContractFields Fields, ProductionNorm Norm,
    ProductionCoverage Coverage, bool IsSynthetic, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SteelDeliveryDto(Guid Id, Guid ContractId, long Version, SteelDeliveryFields Fields,
    long CreatedBy, DateTimeOffset CreatedAt, long UpdatedBy, DateTimeOffset UpdatedAt, bool BeforeOpening);
public sealed record SteelDeliveryResult(SteelDeliveryDto Delivery, long ContractVersion);
public sealed record ProductionChangeDto(Guid Id, long ActorId, string Action, string Reason, string Changes, DateTimeOffset RecordedAt);
public sealed record ProductionFeatures(bool Read, bool Write, DateOnly Today);
