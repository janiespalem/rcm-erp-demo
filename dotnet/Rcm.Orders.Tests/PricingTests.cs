using System.Text.Json;
using Xunit;

namespace Rcm.Orders.Tests;

public sealed class PricingTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static IEnumerable<object[]> PythonCases()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "pricing.json")));
        Assert.Equal("3.11", fixtures.RootElement.GetProperty("oracle").GetProperty("python").GetString());
        foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(PythonCases))]
    public void Matches_every_python_result_field(string name, string fixture)
    {
        using var data = JsonDocument.Parse(fixture);
        var input = data.RootElement.GetProperty("input").Deserialize<StructuredQuoteInput>(Json)!;
        var expected = data.RootElement.GetProperty("expected").Deserialize<StructuredQuoteResult>(Json)!;
        var actual = PricingCalculator.Calculate(input);

        Assert.True(expected == actual, $"{name}: expected {expected}; actual {actual}");
        var processTotals = data.RootElement.GetProperty("process_totals").EnumerateArray().ToArray();
        Assert.Equal(input.Processes.Count, processTotals.Length);
        for (var i = 0; i < processTotals.Length; i++)
            Assert.Equal(processTotals[i].GetDouble(), PricingCalculator.ProcessTotal(input.Processes[i]));
    }

    [Fact]
    public void Explicit_assignments_and_record_updates_preserve_process_presence()
    {
        var legacy = new ProcessLine { Cost = 250 };
        Assert.Equal(250, PricingCalculator.ProcessTotal(legacy));
        Assert.Equal(0, PricingCalculator.ProcessTotal(legacy with { Hours = 0 }));
        Assert.Equal(0, PricingCalculator.ProcessTotal(legacy with { RatePerHour = null }));
        Assert.Equal(180, PricingCalculator.ProcessTotal(legacy with { Hours = 2, RatePerHour = 90 }));
        Assert.Equal(250, PricingCalculator.ProcessTotal(legacy));
    }

    [Fact]
    public void Fractional_material_sum_preserves_production_python_311_total()
    {
        var result = PricingCalculator.Calculate(new()
        {
            Materials = Enumerable.Range(0, 10).Select(_ => new MaterialLine { Cost = 0.1 }).ToArray()
        });

        Assert.Equal(0.9999999999999999, result.MaterialTotal);
        Assert.Equal(1.37, result.TotalNet);
    }
}
