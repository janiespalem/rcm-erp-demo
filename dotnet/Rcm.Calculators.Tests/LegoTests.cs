using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Calculators.Tests;

public sealed class LegoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonDocument Fixtures() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","lego.json")));
    public static IEnumerable<object[]> Plans()
    {
        using var fixtures=Fixtures();
        foreach (var item in fixtures.RootElement.GetProperty("plans").EnumerateArray()) yield return [item.GetRawText()];
    }
    public static IEnumerable<object[]> Courses()
    {
        using var fixtures=Fixtures();
        foreach (var item in fixtures.RootElement.GetProperty("courses").EnumerateArray()) yield return [item.GetRawText()];
    }
    [Theory,MemberData(nameof(Courses))]
    public void Course_matches_original_optimizer(string value)
    {
        using var item=JsonDocument.Parse(value); var root=item.RootElement;
        var series=LegoCalculator.Series.Single(s=>s.Key==root.GetProperty("series").GetString());
        Assert.Equal(root.GetProperty("expected").Deserialize<int[]>(Json),LegoCalculator.MakeCourse(root.GetProperty("length").GetInt32(),series.LengthsCm,
            root.GetProperty("previous").Deserialize<int[]>(Json),root.GetProperty("offset").GetInt32()));
    }
    [Theory,MemberData(nameof(Plans))]
    public void Plan_preserves_layout_and_geometry_with_synthetic_prices(string value)
    {
        using var item=JsonDocument.Parse(value);var root=item.RootElement;
        var input=root.GetProperty("input").Deserialize<LegoInput>(Json)!;
        if (root.TryGetProperty("rejected",out _)) { Assert.Throws<ArgumentException>(()=>LegoCalculator.Calculate(input)); return; }
        var actual=LegoCalculator.Calculate(input);var expected=root.GetProperty("expected");
        Assert.Equal(JsonSerializer.Serialize(expected.GetProperty("courses").Deserialize<LegoCourse[]>(Json),Json),JsonSerializer.Serialize(actual.Courses,Json));
        Assert.Equal(JsonSerializer.Serialize(expected.GetProperty("blocks").Deserialize<LegoBlock[]>(Json),Json),JsonSerializer.Serialize(actual.Blocks,Json));
        Assert.Equal(JsonSerializer.Serialize(expected.GetProperty("counts").Deserialize<LegoCount[]>(Json),Json),JsonSerializer.Serialize(actual.Counts,Json));
        Assert.Equal(expected.GetProperty("quantity").GetInt32(),actual.Quantity);
        Assert.InRange(Math.Abs(expected.GetProperty("weightT").GetDecimal()-actual.WeightT),0,.0000001m);
        Assert.Equal(expected.GetProperty("blockCostPln").GetDecimal(),actual.BlockCostPln);
        Assert.Equal(expected.GetProperty("archQuantity").GetInt32(),actual.ArchQuantity);
        Assert.Equal(expected.GetProperty("archCostPln").GetDecimal(),actual.ArchCostPln);
        Assert.Equal(expected.GetProperty("missingArchPrice").GetBoolean(),actual.MissingArchPrice);
        Assert.Equal(expected.GetProperty("missingPricesCm").Deserialize<int[]>(Json),actual.MissingPricesCm);
        Assert.Equal(expected.GetProperty("rows").GetInt32(),actual.Rows);
        Assert.Equal(expected.GetProperty("actualHeightCm").GetInt32(),actual.ActualHeightCm);
        Assert.Equal(expected.GetProperty("moduleCm").GetInt32(),actual.ModuleCm);
    }
    [Fact]
    public void Impossible_partial_fill_warns_without_inventing_blocks()
    {
        var plan=LegoCalculator.Calculate(new("lego4090","prosta",[.8],.4));
        Assert.Empty(plan.Counts); Assert.NotEmpty(plan.Warnings);
    }
    [Fact]
    public void Cancellation_stops_layout()
    {
        Assert.Throws<OperationCanceledException>(()=>LegoCalculator.Calculate(new("lego60std","prosta",[7.2],1.2),new(true)));
    }
    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(201)]
    public void Rejects_invalid_dimensions(double dimension) => Assert.Throws<ArgumentException>(()=>LegoCalculator.Calculate(new("lego60std","prosta",[dimension],1.2)));
}
