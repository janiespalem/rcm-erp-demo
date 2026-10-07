using Rcm.Contracts;

namespace Rcm.Calculators;

public static class TetrapodCoverage
{
    public static ProductionCoverage Calculate(SteelMasses accounted, ProductionNorm norm, int? planned)
    {
        ArgumentNullException.ThrowIfNull(accounted);
        ArgumentNullException.ThrowIfNull(norm);
        ArgumentNullException.ThrowIfNull(norm.GramsPerTetrapod);
        if (planned is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(planned));
        if (norm.BasketsPerTetrapod is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(norm));
        var rows = new[]
        {
            (Diameter: 6, Mass: accounted.Diameter6, Required: norm.GramsPerTetrapod.Diameter6),
            (Diameter: 12, Mass: accounted.Diameter12, Required: norm.GramsPerTetrapod.Diameter12),
            (Diameter: 16, Mass: accounted.Diameter16, Required: norm.GramsPerTetrapod.Diameter16)
        };
        if (rows.Any(row => row.Required is null or <= 0 or > 1_000_000_000))
            throw new ArgumentOutOfRangeException(nameof(norm));
        long? potential = rows.All(row => row.Mass is not null)
            ? rows.Min(row => Math.Max(0, row.Mass!.Value) / row.Required!.Value)
            : null;
        var limiting = potential is null ? [] : rows
            .Where(row => Math.Max(0, row.Mass!.Value) / row.Required!.Value == potential)
            .Select(row => row.Diameter).ToArray();
        long? Missing(int diameter)
        {
            var row = rows.Single(row => row.Diameter == diameter);
            return planned is null || row.Mass is null ? null : Math.Max(0, checked(row.Required!.Value * planned.Value - row.Mass.Value));
        }
        return new(accounted, potential, potential is null ? null : checked(potential.Value * norm.BasketsPerTetrapod),
            limiting, new(Missing(6), Missing(12), Missing(16)));
    }
}
