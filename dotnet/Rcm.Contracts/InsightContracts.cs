namespace Rcm.Contracts;

public sealed record InsightFeatures(bool Analytics, bool Production, bool Schedule, bool Profitability, bool Benchmark, bool ServiceHistory, bool Export, bool Native, int ProfitabilityWindow = 50, int HistoryWindow = 500);
public sealed record ProductionInsight(long Id, string? OrderNumber, string Client, string Status, DateOnly? Deadline, string? Description, string? Material, string[] Routing, double? TotalNet);
public sealed record ScheduleInsight(long Id, string? OrderNumber, string Client, string Status, DateOnly? Deadline, string? Branch);
// MarginPct and analytical percentage fields use percentage points: 25 means 25%.
public sealed record ProfitabilityInsight(long Id, string OrderNumber, string Client, string Status, double PricePln, double? MaterialCostPln, double? LaborCostPln, double CostPln, double MarginPln, double? MarginPct, double? ActualHours);
public sealed record RevenueMonthInsight(string Month, int Orders, double RevenuePln);
public sealed record TopClientInsight(string Client, int Orders, double RevenuePln);
public sealed record AnalyticsInsight(int TotalOrders, int RejectedCount, double RejectedPct, int StandardCount, int CustomCount,
    double? AverageMarginPct, int OrdersInProduction, int OrdersDone, double? AverageCycleDays, double? AverageQuoteToStartDays,
    double? EstimateAccuracyPct, RevenueMonthInsight[] RevenueByMonth, TopClientInsight[] TopClients, ScheduleInsight[] OverdueOrders, int OverdueTotal);
public sealed record BenchmarkSampleInsight(long OrderId, DateOnly Date, double WeightKg, double TotalNet, double PlnKg);
public sealed record BenchmarkInsight(double AveragePlnKg, double MinimumPlnKg, double MaximumPlnKg, int Count, string? Warning, Page<BenchmarkSampleInsight> Samples);
public sealed record ServiceHistoryInsight(long Id, DateOnly? OrderDate, string? Client, string? OrderType, string? Description, string? Material,
    double? MaterialCost, double? ConstructorHours, double? ProductionHours, double TotalPrice, string? Source, string? SourceOrderNumber);
