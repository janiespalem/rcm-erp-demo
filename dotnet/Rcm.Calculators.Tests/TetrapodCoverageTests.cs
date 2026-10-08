using Rcm.Contracts;
using Xunit;

namespace Rcm.Calculators.Tests;

public sealed class TetrapodCoverageTests
{
    private static readonly ProductionNorm Norm = new(4, new(3768, 3268, 49056));

    [Theory]
    [InlineData(3768, 3268, 49056, 1, 4)]
    [InlineData(3767, 3268, 49056, 0, 0)]
    [InlineData(3768, 3267, 49056, 0, 0)]
    [InlineData(3768, 3268, 49055, 0, 0)]
    [InlineData(0, 3268, 1000000, 0, 0)]
    [InlineData(-1, 3268, 49056, 0, 0)]
    public void Each_required_diameter_limits_whole_products(long six, long twelve, long sixteen, long products, long baskets)
    {
        var result = TetrapodCoverage.Calculate(new(six, twelve, sixteen), Norm, 1);
        Assert.Equal(products, result.PotentialTetrapods); Assert.Equal(baskets, result.PotentialBaskets);
    }

    [Fact]
    public void Excess_total_weight_cannot_replace_missing_diameter()
    {
        var result = TetrapodCoverage.Calculate(new(0, 0, 56092000), Norm, 1000);
        Assert.Equal(0, result.PotentialTetrapods); Assert.Equal(new[] { 6, 12 }, result.LimitingDiameters);
        Assert.Equal(new SteelMasses(3768000, 3268000, 0), result.MissingForPlan);
    }

    [Fact]
    public void Unknown_stock_and_unknown_plan_stay_unknown()
    {
        var result = TetrapodCoverage.Calculate(new(null, 0, 49056), Norm, null);
        Assert.Null(result.PotentialTetrapods); Assert.Null(result.PotentialBaskets);
        Assert.Empty(result.LimitingDiameters); Assert.Equal(new SteelMasses(), result.MissingForPlan);
        result = TetrapodCoverage.Calculate(new(null, 3268, 49056), Norm, 2);
        Assert.Equal(new SteelMasses(null, 3268, 49056), result.MissingForPlan);
    }

    [Fact]
    public void Negative_balance_is_visible_and_does_not_become_available_material()
    {
        var result = TetrapodCoverage.Calculate(new(-100, 3268, 49056), Norm, 1);
        Assert.Equal(-100, result.AccountedSteel.Diameter6);
        Assert.Equal(0, result.PotentialTetrapods); Assert.Equal(new[] { 6 }, result.LimitingDiameters);
        Assert.Equal(3868, result.MissingForPlan.Diameter6);
    }

    [Fact]
    public void Available_material_is_not_clamped_to_contract_quantity()
    {
        var result = TetrapodCoverage.Calculate(new(7536, 6536, 98112), Norm, 1);
        Assert.Equal(2, result.PotentialTetrapods); Assert.Equal(8, result.PotentialBaskets);
        Assert.Equal(new SteelMasses(0, 0, 0), result.MissingForPlan);
    }

    [Fact]
    public void Invalid_norm_is_rejected_before_division()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TetrapodCoverage.Calculate(new(1, 1, 1), new(4, new(0, 1, 1)), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TetrapodCoverage.Calculate(new(1, 1, 1), new(0, new(1, 1, 1)), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TetrapodCoverage.Calculate(new(1, 1, 1), Norm, -1));
    }
}
