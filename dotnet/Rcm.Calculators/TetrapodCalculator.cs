using Rcm.Contracts;

namespace Rcm.Calculators;

public static class TetrapodCalculator
{
    private sealed record Part(string Key, string Name, int Diameter, int Length, int Quantity, double Weight, string? Dimensions = null);
    private static readonly Part[] Parts =
    [
        new("ringSmall", "Krąg mały", 6, 1220, 1, 0.271),
        new("ringMedium", "Krąg średni", 6, 1400, 1, 0.311),
        new("ringLarge", "Krąg duży", 6, 1620, 1, 0.360),
        new("ear12", "Ucho", 12, 920, 1, 0.817, "500×360"),
        new("rod16", "Pręt", 16, 1110, 7, 1.752)
    ];

    public static TetrapodPlan Calculate(TetrapodInput input)
    {
        Count(input.Planned, "planned"); Count(input.Completed, "completed");
        if (input.Completed > input.Planned)
            throw new ArgumentException("Wykonana liczba nie może przekraczać planu.", "completed");
        var counts = new PlanAmounts(input.Planned, input.Completed, input.Planned - input.Completed);
        var baskets = new PlanAmounts(counts.Planned * 4, counts.Completed * 4, counts.Remaining * 4);
        var parts = Parts.Select(p => new TetrapodPart(p.Key, p.Name, p.Diameter, p.Length, p.Quantity, p.Weight, p.Dimensions,
            new(p.Quantity, p.Quantity * 4, p.Quantity * baskets.Planned, p.Quantity * baskets.Completed, p.Quantity * baskets.Remaining),
            Weights(Round(p.Weight * p.Quantity, 1000), baskets))).ToArray();
        var weights = Weights(Round(parts.Sum(p => p.WeightsKg.PerBasket), 1000), baskets);
        var diameters = new[] { 6, 12, 16 }.Select(diameter =>
        {
            var rows = Parts.Where(p => p.Diameter == diameter).ToArray();
            DiameterAmounts Amount(double count) => new(rows.Sum(p => p.Quantity * count),
                Round(rows.Sum(p => p.Length * p.Quantity * count) / 1000, 100),
                Round(rows.Sum(p => p.Weight * p.Quantity * count), 1000));
            var delivered = input.DeliveriesKg?.GetValueOrDefault(diameter);
            if (delivered is { } value && (!double.IsFinite(value) || value < 0))
                throw new ArgumentException("Masa dostawy musi być nieujemną liczbą.", "deliveriesKg");
            var planned = Amount(baskets.Planned); var completed = Amount(baskets.Completed);
            return new TetrapodDiameter(diameter, planned, completed, Amount(baskets.Remaining), delivered,
                delivered is null ? null : completed.WeightKg,
                delivered is null ? null : Round(delivered.Value - completed.WeightKg, 1000),
                delivered is null ? null : Round(delivered.Value - planned.WeightKg, 1000));
        }).ToArray();
        return new(counts, baskets, weights, parts, diameters);
    }

    private static void Count(double value, string field)
    {
        if (!double.IsFinite(value) || value < 0 || Math.Truncate(value) != value)
            throw new ArgumentException("Liczby tetrapodów muszą być pełne i nieujemne.", field);
    }

    private static PartAmounts Weights(double perBasket, PlanAmounts baskets) => new(perBasket,
        Round(perBasket * 4, 1000), Round(perBasket * baskets.Planned, 1000),
        Round(perBasket * baskets.Completed, 1000), Round(perBasket * baskets.Remaining, 1000));

    private static double Round(double value, double scale)
    {
        // Match the existing JavaScript Math.round(value + Number.EPSILON), including negative balances.
        var scaled = (value + 2.220446049250313e-16) * scale;
        var lower = Math.Floor(scaled);
        var rounded = (scaled - lower >= 0.5 ? lower + 1 : lower) / scale;
        if (!double.IsFinite(rounded)) throw new ArgumentException("Wynik przekracza zakres obliczeń.", "planned");
        return rounded == 0 ? 0 : rounded;
    }
}
