namespace Rcm.Contracts;

public record OrderMaterialDto(string? Name = null, double QtyKg = 0);
public record OrderFields(string Client, DateOnly Deadline, string? OrderNumber = null,
    long? ApprovedMaterialId = null, string? Material = null, OrderMaterialDto[]? MaterialsJson = null,
    bool HasDrawing = false, string OrderType = "remont", string? SopName = null, string? Purpose = null,
    string? Notes = null, double EstimatedValue = 0, string? Description = null, bool RequiresVisit = false,
    long? TemplateId = null, int Quantity = 1, bool IsDefence = false, bool IsInternal = false,
    double? WeightKg = null, string? DrawingNumber = null, string? Dimensions = null,
    string? DeliveryAddress = null, string? Contact = null);
public record CreateOrder(Guid RequestId, OrderFields Fields);
public record EditOrder(long ExpectedVersion, OrderFields Fields);
public sealed record OrderDto
{
    public long Id { get; init; }
    public long VersionId { get; init; }
    public string? OrderNumber { get; init; }
    public string Client { get; init; } = "";
    public string Status { get; init; } = "draft";
    public string? TriageBranch { get; init; }
    public DateOnly? Deadline { get; init; }
    public long? ApprovedMaterialId { get; init; }
    public string? Material { get; init; }
    public OrderMaterialDto[] MaterialsJson { get; init; } = [];
    public bool HasDrawing { get; init; }
    public string? OrderType { get; init; }
    public string? SopName { get; init; }
    public string? Purpose { get; init; }
    public string? Notes { get; init; }
    public double? EstimatedValue { get; init; }
    public string? Description { get; init; }
    public bool RequiresVisit { get; init; }
    public long? TemplateId { get; init; }
    public int? Quantity { get; init; }
    public bool IsDefence { get; init; }
    public bool IsInternal { get; init; }
    public double? WeightKg { get; init; }
    public string? DrawingNumber { get; init; }
    public string? Dimensions { get; init; }
    public string? DeliveryAddress { get; init; }
    public string? Contact { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? QuotedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public int PendingQuestions { get; init; }
}
public record OrderEventDto(long Id, string EventType, string? OldStatus, string? NewStatus,
    string? UserName, string? UserRole, string? Note, DateTimeOffset? CreatedAt);
public record OrderIntakeResult(OrderDto Order, OrderTriageResult Triage);
public record OrderTriageResult(string Branch, string Message, long? TemplateId, string? RuleName, string[] Warnings);
public record OrderTemplateDto(long Id, string Name, string? Category, double? BasePricePln,
    double MarginPct, bool IsActive, string? ProjectCode, string? PositionNr, string? Notes);
public record OrderApprovedMaterialDto(long Id, string Name, string? Category, double? DefaultRatePlnKg, bool IsActive, string? Notes);
public record OrderOperationDto(long Id, string Name, string? Department, double? DefaultRate, string? Formula);
public record OrderLookups(OrderTemplateDto[] Templates, OrderApprovedMaterialDto[] Materials,
    OrderOperationDto[] Operations, double LaborRate);
public record OrderQuoteProcess(string Name, string? Department = null, string? Material = null,
    double? Hours = null, double? RatePerHour = null, double Cost = 0);
public record OrderQuoteMaterial(string? Name = null, string? Material = null, double QtyKg = 0, double PricePerKg = 0, double Cost = 0);
public sealed record OrderQuoteInput
{
    public OrderQuoteProcess[] Processes { get; init; } = [];
    public string Method { get; init; } = "kalkulacja";
    public string WeightBasis { get; init; } = "netto";
    public OrderQuoteMaterial[] Materials { get; init; } = [];
    public double MaterialWeightKg { get; init; }
    public double MaterialPricePerKg { get; init; }
    public double MaterialCost { get; init; }
    public double WeightNettoKg { get; init; }
    public double WeightBruttoKg { get; init; }
    public double LaborHours { get; init; }
    public double OverheadPct { get; init; } = .10;
    public double MarginPct { get; init; } = .25;
    public double TransportCost { get; init; }
    public bool ShowUnitPrices { get; init; } = true;
    public double WeightKg { get; init; }
    public double WeightRatePlnKg { get; init; }
}
public record OrderManualQuote(double TotalNet);
public record OrderQuotePreview(double OpsTotal, double MaterialTotal, double ExtraLabor,
    double WeightTotal, double Base, double Subtotal, double TotalNet, string PricingMethod, string WeightBasis);
public record OrderQuoteDto(long Id, long OrderId, double? TotalNet, string? PricingMethod,
    string? WeightBasis, double? LaborHours, double? MaterialCost, double? MarginPct,
    double? TransportCost, DateTimeOffset CreatedAt, DateTimeOffset? LastEditedAt)
{
    public OrderQuoteProcess[]? ProcessesJson { get; init; }
    public OrderQuoteMaterial[]? MaterialsJson { get; init; }
    public double? OverheadPct { get; init; }
    public double? MaterialWeightKg { get; init; }
    public double? MaterialPricePerKg { get; init; }
    public double? WeightKg { get; init; }
    public double? WeightRatePlnKg { get; init; }
    public double? WeightNettoKg { get; init; }
    public double? WeightBruttoKg { get; init; }
    public bool ShowUnitPrices { get; init; } = true;
}

public record OrderFeatures(bool Resources, bool Attachments = false, bool Documents = false, bool Templates = false, bool Intake = false);
