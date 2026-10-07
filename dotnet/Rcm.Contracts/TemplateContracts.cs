using System.Text.Json;

namespace Rcm.Contracts;

public sealed record ProductTemplateDto(long Id, string Name, string Category, JsonElement Operations, JsonElement Materials,
    JsonElement Instructions, JsonElement Machines, double? BasePricePln, double MarginPct, bool IsActive,
    string? ProjectCode, string? PositionNumber, string? Notes, bool HasDrawing, long Version);
public sealed record ProductTemplateDraft(string Name, string Category, JsonElement Operations, JsonElement Materials,
    JsonElement Instructions, JsonElement Machines, double? BasePricePln, double MarginPct,
    string? ProjectCode = null, string? PositionNumber = null, string? Notes = null);
public sealed record CreateProductTemplate(Guid RequestId, ProductTemplateDraft Draft);
public sealed record UpdateProductTemplate(Guid RequestId, long ExpectedVersion, ProductTemplateDraft Draft);
public sealed record ProductTemplateVersionCommand(Guid RequestId, long ExpectedVersion);
public sealed record TemplateProjectDto(string Code, int PositionsCount);
public sealed record TemplateFeatures(bool Read, bool Write, bool Drawings, bool Documents, bool Extract, double LaborRatePln);
public sealed record TemplateDrawingPreview(string? DrawingNumber, string Name, string? Material, double? MassKg,
    string? Profile, int Pages, JsonElement Materials, JsonElement Operations, string RawTextPreview);
public sealed record ApplyTemplateDrawing(Guid RequestId, long ExpectedVersion);
