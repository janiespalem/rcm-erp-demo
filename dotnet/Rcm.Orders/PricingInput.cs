namespace Rcm.Orders;

public sealed record ProcessLine
{
    private double? hours;
    private double? ratePerHour;

    public double? Hours
    {
        get => hours;
        init { hours = value; HoursSupplied = true; }
    }

    public double? RatePerHour
    {
        get => ratePerHour;
        init { ratePerHour = value; RatePerHourSupplied = true; }
    }

    public double? Cost { get; init; }
    internal bool HoursSupplied { get; private init; }
    internal bool RatePerHourSupplied { get; private init; }
}

public sealed record MaterialLine
{
    public double? QtyKg { get; init; }
    public double? PricePerKg { get; init; }
    public double? Cost { get; init; }
}

public sealed record StructuredQuoteInput
{
    public IReadOnlyList<ProcessLine> Processes { get; init; } = [];
    public IReadOnlyList<MaterialLine>? Materials { get; init; }
    public double MaterialCost { get; init; }
    public double MaterialWeightKg { get; init; }
    public double MaterialPricePerKg { get; init; }
    public double LaborHours { get; init; }
    public double LaborRate { get; init; } = 90;
    public double OverheadPct { get; init; } = 0.10;
    public double MarginPct { get; init; } = 0.25;
    public double TransportCost { get; init; }
    public double? WeightKg { get; init; }
    public double? WeightRatePlnKg { get; init; }
    public string? Method { get; init; } = "kalkulacja";
}

public sealed record StructuredQuoteResult(
    double OpsTotal,
    double MaterialTotal,
    double ExtraLabor,
    double WeightTotal,
    double Base,
    double Subtotal,
    double TotalNet,
    string PricingMethod);
