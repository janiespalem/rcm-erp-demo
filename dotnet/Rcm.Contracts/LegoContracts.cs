namespace Rcm.Contracts;

public sealed record LegoInput(string Series, string Shape, double[] DimensionsM, double HeightM, int Boxes = 1, bool WithArch = false, decimal ArchUnitPricePln = 0);
public sealed record LegoSeries(string Key, string Name, int DepthCm, int RowHeightCm, int[] LengthsCm, Dictionary<int, decimal> WeightsT, Dictionary<int, decimal> PricesPln);
public sealed record LegoCourse(string Key, int Row, int OffsetCm, int LengthCm, int[] BlocksCm);
public sealed record LegoBlock(int LengthCm, int Row, int Xcm, int Ycm, int Zcm, int WidthCm, int HeightCm, int DepthCm);
public sealed record LegoCount(int LengthCm, int Quantity, decimal UnitWeightT, decimal? UnitPricePln);
public sealed record LegoPlan(LegoInput Input, int[] DimensionsCm, int Rows, int ActualHeightCm, int ModuleCm,
    LegoCourse[] Courses, LegoBlock[] Blocks, LegoCount[] Counts, int Quantity, decimal WeightT, decimal BlockCostPln,
    int ArchQuantity, decimal ArchCostPln, bool MissingArchPrice, int[] MissingPricesCm, string[] Warnings)
{
    public decimal KnownNetPln => BlockCostPln + ArchCostPln;
    public bool Complete => MissingPricesCm.Length == 0 && !MissingArchPrice;
    public int MinimumTrips => (int)Math.Ceiling(WeightT / 27);
    public decimal ConcreteVolumeM3 => WeightT / 2.4m;
}
public sealed record LegoFixedProduct(string Key, string Name, string Details, decimal WeightT, decimal PricePln);
