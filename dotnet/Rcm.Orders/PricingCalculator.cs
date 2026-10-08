using System.Globalization;
using System.Numerics;

namespace Rcm.Orders;

public static class PricingCalculator
{
    public static double ProcessTotal(ProcessLine process) =>
        process.HoursSupplied || process.RatePerHourSupplied
            ? (process.Hours ?? 0) * (process.RatePerHour ?? 0)
            : process.Cost ?? 0;

    public static StructuredQuoteResult Calculate(StructuredQuoteInput input)
    {
        var opsTotal = Sum(input.Processes.Select(ProcessTotal));
        var materialTotal = input.Materials is { Count: > 0 }
            ? Sum(input.Materials.Select(MaterialTotal))
            : input.MaterialPricePerKg > 0
                ? input.MaterialWeightKg * input.MaterialPricePerKg
                : input.MaterialCost;
        var extraLabor = input.LaborHours * input.LaborRate;
        var method = string.IsNullOrEmpty(input.Method) ? "kalkulacja" : input.Method;
        var weightTotal = (input.WeightKg ?? 0) * (input.WeightRatePlnKg ?? 0);
        double basis, subtotal, totalNet;
        if (method == "od_masy")
        {
            basis = weightTotal;
            subtotal = basis;
            totalNet = RoundCents(basis + input.TransportCost);
        }
        else
        {
            weightTotal = 0;
            basis = opsTotal + materialTotal + extraLabor;
            subtotal = basis * (1 + input.OverheadPct);
            totalNet = RoundCents(subtotal * (1 + input.MarginPct) + input.TransportCost);
        }
        return new(opsTotal, materialTotal, extraLabor, weightTotal, basis, subtotal, totalNet, method);
    }

    private static double MaterialTotal(MaterialLine line) =>
        line.Cost is > 0 ? line.Cost.Value : (line.QtyKg ?? 0) * (line.PricePerKg ?? 0);

    private static double Sum(IEnumerable<double> values)
    {
        // Production Python 3.11 sums sequentially; compensated sums can change cents.
        double total = 0;
        foreach (var value in values)
            total += value;
        return total;
    }

    private static double RoundCents(double value)
    {
        if (!double.IsFinite(value) || value == 0)
            return value;
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000f_ffff_ffff_ffff;
        var exponent = exponentBits == 0 ? -1074 : exponentBits - 1023 - 52;
        if (exponent >= 0)
            return value;
        if (exponentBits != 0)
            significand |= 1UL << 52;

        // Round the exact binary value, not a pre-rounded value * 100 (2.675 -> 2.67).
        var denominator = BigInteger.One << -exponent;
        var cents = BigInteger.DivRem(new BigInteger(significand) * 100, denominator, out var remainder);
        var midpoint = (remainder * 2).CompareTo(denominator);
        if (midpoint > 0 || midpoint == 0 && !cents.IsEven)
            cents++;

        // Parsing the decimal cents avoids double rounding when cents exceed 2^53.
        var digits = cents.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
        var rounded = double.Parse(digits.Insert(digits.Length - 2, "."), CultureInfo.InvariantCulture);
        return Math.CopySign(rounded, value);
    }
}
