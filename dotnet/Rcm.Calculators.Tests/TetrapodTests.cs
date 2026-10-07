using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Calculators.Tests;

public sealed class TetrapodTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> WebCases()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tetrapod.json")));
        foreach (var item in fixtures.RootElement.EnumerateArray())
            yield return [item.GetProperty("input").GetRawText(), item.GetProperty("expected").GetRawText()];
    }

    [Theory]
    [MemberData(nameof(WebCases))]
    public void Matches_every_web_result_field(string input, string expected)
    {
        var request = JsonSerializer.Deserialize<TetrapodInput>(input, Json)!;
        var actual = TetrapodCalculator.Calculate(request);
        var original = JsonSerializer.Deserialize<TetrapodPlan>(expected, Json)!;
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(actual, Json));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2.5, 0)]
    [InlineData(2, 3)]
    [InlineData(2, -1)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Rejects_impossible_counts(double planned, double completed) =>
        Assert.Throws<ArgumentException>(() => TetrapodCalculator.Calculate(new(planned, completed)));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_invalid_delivered_weight(double weight) =>
        Assert.Throws<ArgumentException>(() => TetrapodCalculator.Calculate(new(1, 0, new() { [6] = weight })));
}
