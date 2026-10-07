namespace Rcm.Contracts;

public record TetrapodInput(double Planned, double Completed, Dictionary<int, double?>? DeliveriesKg = null);
public record PlanAmounts(double Planned, double Completed, double Remaining);
public record PartAmounts(double PerBasket, double PerTetrapod, double Planned, double Completed, double Remaining);
public record TetrapodPart(string Key, string Name, int DiameterMm, int LengthMm, int QuantityPerBasket,
    double UnitWeightKg, string? Dimensions, PartAmounts Quantities, PartAmounts WeightsKg);
public record DiameterAmounts(double Quantity, double LengthM, double WeightKg);
public record TetrapodDiameter(int DiameterMm, DiameterAmounts Planned, DiameterAmounts Completed,
    DiameterAmounts Remaining, double? DeliveryKg, double? ConsumedKg, double? AvailableKg, double? BalanceKg);
public record TetrapodPlan(PlanAmounts Tetrapods, PlanAmounts Baskets, PartAmounts WeightsKg,
    TetrapodPart[] Parts, TetrapodDiameter[] Diameters);
