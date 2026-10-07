namespace Rcm.Contracts;

public sealed record CatalogMaterialDto(long Id, string Name, string? Category, double? DefaultRatePlnKg, bool IsActive, string? Notes, long Version);
public sealed record CatalogOperationDto(long Id, string Name, string? Department, double? DefaultRate, string? Formula, long Version);
public sealed record CreateCatalogMaterial(Guid RequestId, string Name, string? Category = null, double? DefaultRatePlnKg = null, bool IsActive = true, string? Notes = null);
public sealed record UpdateCatalogMaterial(Guid RequestId, long ExpectedVersion, string Name, string? Category = null, double? DefaultRatePlnKg = null, bool IsActive = true, string? Notes = null);
public sealed record CreateCatalogOperation(Guid RequestId, string Name, string? Department = null, double? DefaultRate = null, string? Formula = null);
public sealed record UpdateCatalogOperation(Guid RequestId, long ExpectedVersion, string Name, string? Department = null, double? DefaultRate = null, string? Formula = null);
public sealed record CatalogVersionCommand(Guid RequestId, long ExpectedVersion);
public sealed record CatalogMutationResult(long Id, long Version, bool Deleted);
public sealed record CatalogFeatures(bool Materials, bool Operations, bool Write, double LaborRatePln);
