using Rcm.Contracts;

namespace Rcm.Calculators;

public static class LegoCalculator
{
    public static readonly LegoSeries[] Series =
    [
        new("lego60std", "LEGO 60 standard", 60, 60, [180,120,90,60], new(){{180,1.5m},{120,1m},{90,.75m},{60,.5m}}, new(){{180,125},{120,150},{90,175},{60,200}}),
        new("lego60full", "LEGO 60 pełne", 60, 60, [240,180,120,90,60], new(){{240,2m},{180,1.5m},{120,1m},{90,.75m},{60,.5m}}, new(){{240,225},{180,250},{120,275},{90,300},{60,325}}),
        new("lego60wzor", "LEGO 60 z wzorem", 60, 60, [180,120,60], new(){{180,1.5m},{120,1m},{60,.5m}}, new(){{180,350},{120,375},{60,400}}),
        new("lego80c20", "LEGO 80 C20/25", 80, 80, [160,120,80,40], new(){{160,2.3m},{120,1.73m},{80,1.15m},{40,.58m}}, new(){{160,425},{120,450},{80,475},{40,500}}),
        new("lego80c30", "LEGO 80 C30/37", 80, 80, [160,120,80,40], new(){{160,2.3m},{120,1.73m},{80,1.15m},{40,.58m}}, new(){{160,525},{120,550},{80,575},{40,600}}),
        new("lego4090", "LEGO 40×90", 40, 90, [240,200], new(){{240,2.07m},{200,1.62m}}, new(){{240,625},{200,650}}),
        new("lego40", "LEGO 40×40", 40, 40, [240,120], new(){{240,.92m},{120,.46m}}, new(){{240,675},{120,700}})
    ];
    public static readonly LegoFixedProduct[] Products =
    [
        new("wallSmall", "Mur oporowy typu L", "100×100×160×12 cm · C20/25", .69m, 600),
        new("roadPlate", "Płyta drogowa", "300×150×15 cm · C30/37", 1.5m, 800),
        new("float237x200", "Pływak siatkobetonowy", "237×200×75 cm · C30/37", 2, 1000),
        new("float237x300", "Pływak siatkobetonowy", "237×300×75 cm · C30/37", 3, 1200),
        new("anchorSmall", "Kotwica mała", "C30/37", 1.2m, 1400),
        new("anchorLarge", "Kotwica duża", "C30/37", 2.2m, 1600),
        new("cellar", "Piwniczka betonowa", "C30/37 · wew. 280×220×240 cm · zew. 300×240×260 cm", 8.4m, 1800),
        new("stairs", "Schody do piwniczki", "C30/37 · wejście wew. 185×90 cm", 1.95m, 2000),
        new("rainTank", "Zbiornik na deszczówkę", "C30/37 · wew. 280×220×163 cm · zew. 300×240×190 cm", 9, 2200)
    ];

    public static LegoPlan Calculate(LegoInput input, CancellationToken ct = default)
    {
        var series = Series.SingleOrDefault(s => s.Key == input.Series) ?? throw new ArgumentException("Nieznana seria bloczków.");
        var count = input.Shape switch { "prosta" => 1, "L" => 2, "U" => 3, "boksy" => 2, _ => throw new ArgumentException("Nieznany kształt.") };
        if (input.DimensionsM is null || input.DimensionsM.Length != count || input.DimensionsM.Any(v => !double.IsFinite(v) || v <= 0 || v > 200)
            || !double.IsFinite(input.HeightM) || input.HeightM <= 0 || input.HeightM > 12 || input.Boxes is < 1 or > 50
            || input.ArchUnitPricePln is < 0 or > 1000000 || input.WithArch && input.Shape is not ("U" or "boksy"))
            throw new ArgumentException("Podaj dodatnie wymiary do 200 m, wysokość do 12 m i 1–50 boksów.");
        var raw = input.DimensionsM.Select(v => (int)Math.Floor(v * 100 + .5)).ToArray();
        var d = series.DepthCm; var module = series.LengthsCm.Aggregate(Gcd);
        var lengths = input.Shape switch
        {
            "prosta" => new[] {raw[0]}, "L" => [raw[0],raw[0]-d,raw[1],raw[1]-d],
            "U" => [raw[0],raw[0]-d,raw[1],raw[1]-2*d,raw[2],raw[2]-d],
            _ => [input.Boxes*raw[0]+(input.Boxes+1)*d,input.Boxes*raw[0]+(input.Boxes-1)*d,raw[1],raw[1]+d]
        };
        if (lengths.Any(v => v <= 0 || v % module != 0) || input.Shape != "boksy" && raw.Any(v => v % module != 0))
            throw new ArgumentException($"Wymiar albo odcinek po złożeniu narożnika nie leży w module {module} cm. Skoryguj wymiar.");
        var rows = (int)Math.Ceiling(input.HeightM * 100 / series.RowHeightCm);
        var walls = input.Shape == "boksy" ? input.Boxes+2 : count;
        if ((long)lengths.Max()*rows*walls > 4000000) throw new ArgumentException("Układ jest zbyt duży. Podziel go na mniejsze odcinki.");
        List<LegoCourse> courses = []; Dictionary<string,int[]> previous = [];
        void Push(string key, int row, int offset, int length)
        {
            ct.ThrowIfCancellationRequested();
            if (length <= 0) return;
            var seq = MakeCourse(length, series.LengthsCm, previous.GetValueOrDefault(key) ?? [], offset, ct);
            courses.Add(new(key,row,offset,length,seq));
            var position = offset; previous[key] = seq.Take(Math.Max(0,seq.Length-1)).Select(b => position += b).ToArray();
        }
        for (var r = 0; r < rows; r++)
        {
            var sideOwns = r % 2 == 1;
            switch (input.Shape)
            {
                case "prosta": Push("prosta",r,0,raw[0]); break;
                case "L":
                    if (sideOwns) { Push("B",r,0,raw[1]); Push("A",r,d,Math.Max(raw[0]-d,d)); }
                    else { Push("A",r,0,raw[0]); Push("B",r,d,Math.Max(raw[1]-d,d)); }
                    break;
                case "U":
                    if (sideOwns) { Push("Lewa",r,0,raw[0]); Push("Prawa",r,0,raw[2]); Push("Tylna",r,d,Math.Max(raw[1]-2*d,d)); }
                    else { Push("Tylna",r,0,raw[1]); Push("Lewa",r,d,Math.Max(raw[0]-d,d)); Push("Prawa",r,d,Math.Max(raw[2]-d,d)); }
                    break;
                case "boksy":
                    var back = input.Boxes*raw[0]+(input.Boxes+1)*d;
                    if (sideOwns) { Push("Boczna_1",r,0,raw[1]+d); Push($"Boczna_{input.Boxes+1}",r,0,raw[1]+d); Push("Tylna",r,d,Math.Max(back-2*d,d)); }
                    else { Push("Tylna",r,0,back); Push("Boczna_1",r,0,raw[1]); Push($"Boczna_{input.Boxes+1}",r,0,raw[1]); }
                    for (var i = 2; i <= input.Boxes; i++) Push($"Boczna_{i}",r,0,raw[1]);
                    break;
            }
        }
        var counts = courses.SelectMany(c => c.BlocksCm).GroupBy(b => b).OrderByDescending(g => g.Key)
            .Select(g => new LegoCount(g.Key,g.Count(),series.WeightsT.GetValueOrDefault(g.Key),series.PricesPln.TryGetValue(g.Key,out var price) ? price : null)).ToArray();
        List<string> warnings = [];
        if (courses.Any(c => c.BlocksCm.Sum() != c.LengthCm)) warnings.Add("Nie każdy odcinek można wypełnić dostępnymi bloczkami. Sprawdź układ przed zamówieniem.");
        var archQty = input.WithArch ? input.Shape == "boksy" ? input.Boxes*(int)Math.Ceiling(raw[1]/600d) : (int)Math.Ceiling(raw[0]/600d) : 0;
        // Preserve the existing calculator's arch quantity and width checks until its specification changes.
        if (input.WithArch && (input.Shape == "boksy" ? raw[0] != 5400 : Math.Abs(raw[1]-2*d-5400) > series.LengthsCm[^1]))
            warnings.Add("Łuk wymaga rozpiętości 5,40 m. Sprawdź szerokość i wycenę łuków przed zamówieniem.");
        var blocks = Geometry(input.Shape,courses,series,input.Boxes,raw);
        if (blocks.Length > 20000) throw new ArgumentException("Układ zawiera zbyt wiele bloczków. Podziel go na mniejsze odcinki.");
        return new(input,raw,rows,rows*series.RowHeightCm,module,courses.ToArray(),blocks,counts,counts.Sum(c => c.Quantity),
            counts.Sum(c => c.Quantity*c.UnitWeightT),counts.Sum(c => c.Quantity*(c.UnitPricePln ?? 0)),archQty,archQty*input.ArchUnitPricePln,
            input.WithArch && input.ArchUnitPricePln <= 0,counts.Where(c => c.UnitPricePln is null).Select(c => c.LengthCm).ToArray(),warnings.ToArray());
    }

    public static int[] MakeCourse(int length, int[] blocks, int[]? previous = null, int offset = 0, CancellationToken ct = default)
    {
        if (length is < 0 or > 200000 || blocks.Length == 0 || blocks.Any(b => b <= 0)) throw new ArgumentException("Nieprawidłowy odcinek.");
        var score = new long[length+1]; Array.Fill(score,long.MaxValue); score[0]=0;
        var last = new int[length+1]; var seams = (previous ?? []).ToHashSet();
        for (var pos = 0; pos <= length; pos++)
        {
            if (pos % 1024 == 0) ct.ThrowIfCancellationRequested();
            if (score[pos] == long.MaxValue) continue;
            foreach (var b in blocks)
            {
                var next=pos+b; if (next>length) continue;
                var value=score[pos]+1000+(next<length && seams.Contains(offset+next) ? 100000 : 0)+(pos==0 || next==length ? (blocks[0]-b)*6 : 0)-b;
                if (value<score[next]) { score[next]=value; last[next]=b; }
            }
        }
        if (score[length]==long.MaxValue) return [];
        List<int> seq=[]; for (var end=length;end>0;end-=last[end]) seq.Add(last[end]);
        seq.Reverse(); return seq.ToArray();
    }

    private static int Gcd(int a,int b) => b==0 ? a : Gcd(b,a%b);
    private static LegoBlock[] Geometry(string shape,List<LegoCourse> courses,LegoSeries series,int boxes,int[] raw)
    {
        List<LegoBlock> output=[]; var d=series.DepthCm; var back=courses.Where(c=>c.Key=="Tylna").Select(c=>c.OffsetCm+c.LengthCm).DefaultIfEmpty().Max();
        foreach (var course in courses)
        {
            var alongX=course.Key is "prosta" or "A" or "Tylna";
            var x=course.Key=="Prawa" ? Math.Max(back-d,0) : shape=="boksy" && course.Key.StartsWith("Boczna_") ? (int.Parse(course.Key[7..])-1)*(raw[0]+d) : 0;
            var z=shape=="boksy" && alongX ? raw[1] : 0; var p=course.OffsetCm;
            foreach (var length in course.BlocksCm) { output.Add(new(length,course.Row,alongX ? p : x,course.Row*series.RowHeightCm,alongX ? z : p,alongX ? length : d,series.RowHeightCm,alongX ? d : length)); p+=length; }
        }
        return output.ToArray();
    }
}
