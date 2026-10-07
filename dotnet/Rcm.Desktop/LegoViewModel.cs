using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed record LegoCatalog(LegoSeries[] Series, LegoFixedProduct[] Products);
public sealed record LegoShape(string Key, string Name);
public sealed record LegoCountRow(LegoCount Count)
{
    public string Length => $"{Count.LengthCm} cm";
    public int Quantity => Count.Quantity;
    public string Weight => $"{Count.Quantity * Count.UnitWeightT:N2} t";
    public string UnitPrice => Count.UnitPricePln is { } price ? $"{price:N2} zł" : "brak ceny";
    public string Total => Count.UnitPricePln is { } price ? $"{price * Count.Quantity:N2} zł" : "—";
}
public sealed record LegoCourseRow(LegoCourse Course)
{
    public string Wall => Course.Key.Replace("_", " ");
    public int Row => Course.Row + 1;
    public string Offset => $"{Course.OffsetCm} cm";
    public string Length => $"{Course.LengthCm} cm";
    public string Sequence => Course.BlocksCm.Length == 0 ? "—" : string.Join(" + ", Course.BlocksCm) + " cm";
    public string Warning => Course.BlocksCm.Sum() == Course.LengthCm ? "" : "Odcinek niewypełniony";
}
public sealed partial class LegoCartRow(LegoCartEntry entry) : ObservableObject
{
    public LegoCartEntry Entry { get; } = entry;
    [ObservableProperty] private string quantityText = entry.Quantity.ToString(CultureInfo.InvariantCulture);
    public int? Quantity => int.TryParse(QuantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 999 ? value : null;
    public string Error => Quantity is null ? "Wpisz ilość od 1 do 999." : "";
    public string Name => Entry.Name;
    public string Details => Entry.Details;
    public string Warning => string.Join("\n", Entry.Warnings);
    public string Weight => Quantity is { } quantity ? $"{Entry.UnitWeightT * quantity:N2} t" : "—";
    public string Net => Quantity is { } quantity ? $"{(Entry.Complete ? "" : "znana suma ")}{Entry.UnitKnownNetPln * quantity:N2} zł" : "—";
    partial void OnQuantityTextChanged(string value) { foreach (var key in new[] { nameof(Quantity), nameof(Error), nameof(Weight), nameof(Net) }) OnPropertyChanged(key); }
    public LegoCartEntry Export() => Entry with { Quantity = Quantity ?? throw new InvalidDataException("Popraw ilości od 1 do 999."), Warnings = Entry.Warnings.ToArray() };
}
public sealed partial class LegoViewModel : ObservableObject
{
    private readonly CrmClient api;
    private readonly ILegoCartStore store;
    private readonly long userId;
    private string savedCart = "[]";
    private bool loading;
    private bool installingCatalog;
    public static LegoShape[] Shapes => [new("prosta", "Prosta"), new("L", "L"), new("U", "U"), new("boksy", "Boksy")];
    public ObservableCollection<LegoSeries> Series { get; } = [];
    public ObservableCollection<LegoFixedProduct> Products { get; } = [];
    public ObservableCollection<LegoCountRow> Counts { get; } = [];
    public ObservableCollection<LegoCourseRow> Courses { get; } = [];
    public ObservableCollection<LegoCartRow> Cart { get; } = [];
    [ObservableProperty] private string seriesKey = "lego40";
    [ObservableProperty] private string shape = "boksy";
    [ObservableProperty] private string dimensionA = "5";
    [ObservableProperty] private string dimensionB = "7,2";
    [ObservableProperty] private string dimensionC = "15";
    [ObservableProperty] private string height = "1,6";
    [ObservableProperty] private string boxes = "8";
    [ObservableProperty] private bool withArch;
    [ObservableProperty] private string archPrice = "0";
    [ObservableProperty] private LegoFixedProduct? selectedProduct;
    [ObservableProperty] private LegoCartRow? selectedCartRow;
    [ObservableProperty] private LegoPlan? result;
    [ObservableProperty] private bool isCalculating;
    [ObservableProperty] private bool rendering;
    [ObservableProperty] private bool cartBusy;
    [ObservableProperty] private bool catalogBusy;
    [ObservableProperty] private bool cartLoaded;
    [ObservableProperty] private string status = "Wybierz serię i kształt, następnie oblicz układ.";
    [ObservableProperty] private string cartStatus = "Koszyk jest zapisany wyłącznie na tym komputerze, osobno dla Twojego konta.";
    public LegoViewModel(CrmClient api, ILegoCartStore store, long userId)
    { this.api = api; this.store = store; this.userId = userId; Cart.CollectionChanged += CartChanged; }
    public long UserId => userId;
    public bool Allowed => api.Session?.UserId == userId && api.Session.Role is "biuro" or "technolog";
    public bool IsWorking => IsCalculating || Rendering || CartBusy || CatalogBusy;
    public bool CanEdit => Allowed && !IsCalculating && !Rendering;
    public bool HasResult => Result is not null;
    public bool IsBoxes => Shape == "boksy";
    public bool HasDimensionB => Shape != "prosta";
    public bool HasDimensionC => Shape == "U";
    public bool HasArchOption => Shape is "U" or "boksy";
    public string LabelA => IsBoxes ? "Szerokość boksu (m)" : "Lewa / A (m)";
    public string LabelB => IsBoxes ? "Głębokość boksu (m)" : "Tylna / B (m)";
    public bool CanCalculate => CanEdit && !CatalogBusy && Series.Count > 0;
    public bool CanCartEdit => Allowed && CartLoaded && !CartBusy;
    public bool CanAddResult => CanCartEdit && !IsCalculating && !Rendering && Result is { Quantity: > 0 };
    public bool CanAddProduct => CanCartEdit && SelectedProduct is not null;
    public bool CanRemove => CanCartEdit && SelectedCartRow is not null;
    public bool CanClear => CanCartEdit && Cart.Count > 0;
    public bool CartValid => Cart.All(row => row.Quantity is not null);
    public bool CartDirty => CartSnapshot() != savedCart;
    public bool CanSaveCart => CanCartEdit && CartDirty && CartValid;
    public bool FormDirty => !string.IsNullOrEmpty(SeriesKey) && SeriesKey != "lego40" || Shape != "boksy" || DimensionA != "5" || DimensionB != "7,2" || DimensionC != "15" || Height != "1,6" || Boxes != "8" || WithArch || ArchPrice != "0";
    public bool Dirty => FormDirty || CartDirty;
    public string Summary => Result is not { } plan ? "" : $"{plan.Quantity:N0} bloczków · {plan.Rows} warstw · h {plan.ActualHeightCm / 100d:N2} m\n{plan.WeightT:N2} t · {plan.ConcreteVolumeM3:N2} m³ betonu · min. {plan.MinimumTrips} kursów przy 27 t\n{(plan.Complete ? "Razem netto" : "Znana suma netto")}: {plan.KnownNetPln:N2} zł\n{(plan.Input.WithArch ? $"Łuki: {plan.ArchQuantity} szt. · znana wartość {plan.ArchCostPln:N2} zł" : "")}";
    public string Warnings => Result is not { } plan ? "" : string.Join("\n", PlanWarnings(plan));
    public string CartSummary => !CartValid ? "Popraw ilości przed podsumowaniem i zapisem koszyka." : $"{(Cart.All(row => row.Entry.Complete) ? "Razem netto" : "Znana suma netto")}: {Cart.Sum(row => row.Entry.UnitKnownNetPln * row.Quantity!.Value):N2} zł\nMasa: {Cart.Sum(row => row.Entry.UnitWeightT * row.Quantity!.Value):N2} t · Min. kursów przy 27 t: {Math.Ceiling(Cart.Sum(row => row.Entry.UnitWeightT * row.Quantity!.Value) / 27)}\n{(Cart.Any(row => !row.Entry.Complete) ? "Niepełna wycena. Pełna suma nie jest dostępna." : "")}";
    private static string[] PlanWarnings(LegoPlan plan)
    {
        List<string> warnings = [.. plan.Warnings];
        if (plan.MissingPricesCm.Length > 0) warnings.Add("Brak ceny bloczków: " + string.Join(", ", plan.MissingPricesCm.Select(length => $"{length} cm")) + ". Pokazana kwota jest znaną sumą netto.");
        if (plan.MissingArchPrice) warnings.Add("Brak ceny łuków. Pokazana kwota jest znaną sumą netto.");
        return warnings.ToArray();
    }
    private void CartChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (LegoCartRow row in e.OldItems) row.PropertyChanged -= CartRowChanged;
        if (e.NewItems is not null) foreach (LegoCartRow row in e.NewItems) row.PropertyChanged += CartRowChanged;
        if (!loading) CartStatus = "Koszyk zmieniony. Zapisz go na tym komputerze."; Notify();
    }
    private void CartRowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LegoCartRow.QuantityText)) { CartStatus = "Koszyk zmieniony. Zapisz go na tym komputerze."; Notify(); } }
    private string CartSnapshot() => JsonSerializer.Serialize(Cart.Select(row => new { row.Entry, row.QuantityText }));
    private void InputChanged() { if (installingCatalog) return; Result = null; Counts.Clear(); Courses.Clear(); Status = "Wartości zmienione. Oblicz układ ponownie."; Notify(); }
    partial void OnSeriesKeyChanged(string value) => InputChanged();
    partial void OnShapeChanged(string value) { if (value is not ("U" or "boksy")) WithArch = false; InputChanged(); }
    partial void OnDimensionAChanged(string value) => InputChanged();
    partial void OnDimensionBChanged(string value) => InputChanged();
    partial void OnDimensionCChanged(string value) => InputChanged();
    partial void OnHeightChanged(string value) => InputChanged();
    partial void OnBoxesChanged(string value) => InputChanged();
    partial void OnWithArchChanged(bool value) { if (value) DimensionA = "5,40"; InputChanged(); }
    partial void OnArchPriceChanged(string value) => InputChanged();
    partial void OnResultChanged(LegoPlan? value) => Notify();
    partial void OnIsCalculatingChanged(bool value) => Notify();
    partial void OnRenderingChanged(bool value) => Notify();
    partial void OnCartBusyChanged(bool value) => Notify();
    partial void OnCatalogBusyChanged(bool value) => Notify();
    partial void OnCartLoadedChanged(bool value) => Notify();
    partial void OnSelectedProductChanged(LegoFixedProduct? value) => Notify();
    partial void OnSelectedCartRowChanged(LegoCartRow? value) => Notify();
    public void Notify() { foreach (var key in new[] { nameof(Allowed), nameof(IsWorking), nameof(CanEdit), nameof(HasResult), nameof(IsBoxes), nameof(HasDimensionB), nameof(HasDimensionC), nameof(HasArchOption), nameof(LabelA), nameof(LabelB), nameof(CanCalculate), nameof(CanCartEdit), nameof(CanAddResult), nameof(CanAddProduct), nameof(CanRemove), nameof(CanClear), nameof(CartValid), nameof(CartDirty), nameof(CanSaveCart), nameof(FormDirty), nameof(Dirty), nameof(Summary), nameof(Warnings), nameof(CartSummary) }) OnPropertyChanged(key); }
    public async Task LoadCatalog(CancellationToken ct)
    {
        if (!Allowed || CatalogBusy) return; CatalogBusy = true;
        try
        {
            var catalog = await api.ReadLegoCatalog(ct); ct.ThrowIfCancellationRequested(); if (!Allowed) return;
            if (catalog.Series is null || catalog.Products is null || catalog.Series.Length != 7 || catalog.Series.Select(row => row.Key).Distinct().Count() != 7 || catalog.Products.Length != 9) throw new ApiFailure(502, "Nieprawidłowy katalog LEGO.");
            var selectedKey = SeriesKey; var productKey = SelectedProduct?.Key;
            installingCatalog = true;
            try
            {
                Series.Clear(); foreach (var row in catalog.Series) Series.Add(row); SeriesKey = catalog.Series.Any(row => row.Key == selectedKey) ? selectedKey : "lego40";
                Products.Clear(); foreach (var row in catalog.Products) Products.Add(row); SelectedProduct = Products.FirstOrDefault(row => row.Key == productKey) ?? Products.FirstOrDefault();
            }
            finally { installingCatalog = false; Notify(); }
            Status = "Katalog aktualny. Oblicz układ dla wpisanych wymiarów.";
        }
        finally { CatalogBusy = false; }
    }
    public async Task LoadCart(CancellationToken ct)
    {
        if (!Allowed || CartBusy || CartLoaded) return; CartBusy = true;
        try
        {
            var entries = await store.Read(userId, ct); ct.ThrowIfCancellationRequested(); if (!Allowed) return;
            loading = true; Cart.Clear(); foreach (var row in entries) Cart.Add(new(row)); savedCart = CartSnapshot(); CartLoaded = true;
            CartStatus = "Koszyk wczytany z tego komputera. Zmiany zapisujesz jawnie.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { CartStatus = "Nie udało się wczytać koszyka. Oryginalny plik zachowano. Sprawdź dostęp i ponów wczytanie."; }
        finally { loading = false; CartBusy = false; }
    }
    public async Task<bool> SaveCart(CancellationToken ct)
    {
        if (!CanSaveCart) return false; var entries = Cart.Select(row => row.Export()).ToArray(); var snapshot = CartSnapshot(); CartBusy = true;
        try { await store.Write(userId, entries, ct); ct.ThrowIfCancellationRequested(); savedCart = snapshot; CartStatus = "Koszyk zapisany na tym komputerze dla Twojego konta."; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { CartStatus = "Nie udało się zapisać koszyka. Pozycje zachowano; sprawdź wolne miejsce i uprawnienia, następnie ponów zapis."; return false; }
        finally { CartBusy = false; }
    }
    private LegoInput Input()
    {
        double Dimension(string text, string name, double maximum) { if (!OrderEditorViewModel.TryNumber(text, out var value) || value <= 0 || value > maximum) throw new FormatException($"{name}: podaj liczbę od 0 do {maximum}, większą od zera."); return value; }
        var dimensions = Shape switch { "prosta" => new[] { Dimension(DimensionA, "A", 200) }, "L" or "boksy" => [Dimension(DimensionA, "A", 200), Dimension(DimensionB, "B", 200)], "U" => [Dimension(DimensionA, "A", 200), Dimension(DimensionB, "B", 200), Dimension(DimensionC, "Prawa", 200)], _ => throw new FormatException("Wybierz kształt.") };
        var boxes = 1; var price = 0m;
        if (IsBoxes && (!int.TryParse(Boxes, NumberStyles.None, CultureInfo.InvariantCulture, out boxes) || boxes is < 1 or > 50)) throw new FormatException("Wpisz liczbę boksów od 1 do 50.");
        if (HasArchOption && WithArch && (!decimal.TryParse(ArchPrice.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price) || price is < 0 or > 1_000_000)) throw new FormatException("Cena łuku: podaj kwotę od 0 do 1 000 000.");
        return new(SeriesKey, Shape, dimensions, Dimension(Height, "Wysokość", 12), IsBoxes ? boxes : 1, HasArchOption && WithArch, price);
    }
    public async Task<bool> Calculate(CancellationToken ct)
    {
        if (!CanCalculate) return false; Result = null; Counts.Clear(); Courses.Clear();
        try
        {
            var input = Input(); IsCalculating = true; Status = "Obliczanie układu…";
            var plan = await api.CalculateLego(input, ct); ct.ThrowIfCancellationRequested(); if (!Allowed) return false;
            if (JsonSerializer.Serialize(plan.Input) != JsonSerializer.Serialize(input) || plan.Blocks is null || plan.Counts is null || plan.Courses is null || plan.Rows is < 1 or > 30 || plan.Blocks.Length > 20000 || plan.Blocks.Any(block => block.Xcm < 0 || block.Ycm < 0 || block.Zcm < 0 || block.WidthCm <= 0 || block.HeightCm <= 0 || block.DepthCm <= 0 || block.Row < 0 || block.Row >= plan.Rows) || plan.Quantity < 0 || plan.WeightT < 0 || plan.KnownNetPln < 0) throw new ApiFailure(502, "Wynik nie odpowiada wpisanym wartościom. Oblicz ponownie.");
            foreach (var row in plan.Counts) Counts.Add(new(row)); foreach (var row in plan.Courses) Courses.Add(new(row)); Result = plan;
            Status = "Układ obliczony. Sprawdź warstwy i ostrzeżenia przed dodaniem do lokalnego koszyka."; return true;
        }
        catch (FormatException ex) { Status = ex.Message; }
        catch (ApiFailure ex) { Status = ex.Message + " Wpisane wartości zachowano."; if (ex.Status == 401) throw; }
        catch (HttpRequestException) { Status = "Brak połączenia. Wpisane wartości zachowano; oblicz ponownie."; }
        catch (OperationCanceledException) { Status = "Przerwano oczekiwanie. Wpisane wartości zachowano; oblicz ponownie."; }
        finally { IsCalculating = false; }
        return false;
    }
    public void AddResult()
    {
        if (!CanAddResult || Result is not { } plan) return; if (Cart.Count >= 100) { CartStatus = "Koszyk może zawierać maksymalnie 100 pozycji."; return; }
        var series = Series.Single(row => row.Key == plan.Input.Series); var shape = Shapes.Single(row => row.Key == plan.Input.Shape).Name;
        var details = $"{shape} · {string.Join(" × ", plan.DimensionsCm.Select(value => (value / 100d).ToString("N2")))} m · h {plan.ActualHeightCm / 100d:N2} m · {plan.Quantity} bloczków" + (plan.Input.Shape == "boksy" ? $" · {plan.Input.Boxes} boksów" : "") + (plan.Input.WithArch ? $" · {plan.ArchQuantity} łuków" : "");
        var row = new LegoCartRow(new(Guid.NewGuid(), "lego", series.Name + " · " + shape, details, plan.WeightT, plan.KnownNetPln, plan.Complete, PlanWarnings(plan), 1)); Cart.Add(row); SelectedCartRow = row;
    }
    public void AddProduct()
    {
        if (!CanAddProduct || SelectedProduct is not { } product) return;
        var existing = Cart.FirstOrDefault(row => row.Entry.Kind == product.Key && row.Entry.UnitKnownNetPln == product.PricePln && row.Entry.UnitWeightT == product.WeightT);
        if (existing is not null) { if (existing.Quantity is { } quantity && quantity < 999) existing.QuantityText = (quantity + 1).ToString(CultureInfo.InvariantCulture); return; }
        if (Cart.Count >= 100) { CartStatus = "Koszyk może zawierać maksymalnie 100 pozycji."; return; }
        var row = new LegoCartRow(new(Guid.NewGuid(), product.Key, product.Name, product.Details, product.WeightT, product.PricePln, true, [], 1)); Cart.Add(row); SelectedCartRow = row;
    }
    public void Remove() { if (CanRemove && SelectedCartRow is { } row) { Cart.Remove(row); SelectedCartRow = null; } }
    public void ClearCart() { if (CanClear) { Cart.Clear(); SelectedCartRow = null; } }
}
