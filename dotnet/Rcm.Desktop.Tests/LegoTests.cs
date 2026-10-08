using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Calculators;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class LegoTests
{
    [Theory]
    [InlineData("prosta")] [InlineData("L")] [InlineData("U")] [InlineData("boksy")]
    public async Task Typed_result_preserves_server_courses_geometry_and_input_across_shapes(string shape)
    {
        var server = new NativeLegoServer(); using var api = await Api(server); var model = new LegoViewModel(api, new MemoryLegoCartStore(), 101);
        await model.LoadCatalog(default); await model.LoadCart(default); Assert.Equal(7, model.Series.Count); Assert.Equal(9, model.Products.Count);
        model.SeriesKey = "lego60std"; model.Shape = shape; model.DimensionA = "7,2"; model.DimensionB = "7,2"; model.DimensionC = "7,2"; model.Height = "1,2"; model.Boxes = "2";
        Assert.True(await model.Calculate(default)); Assert.Equal(shape, server.Input!.Shape); Assert.Equal(7.2, server.Input.DimensionsM[0]);
        Assert.Same(model.Result!.Courses[0], model.Courses[0].Course); Assert.Equal(model.Result.Counts.Sum(row => row.Quantity), model.Result.Quantity);
        model.AddResult(); Assert.Single(model.Cart); Assert.Equal(model.Result.WeightT, model.Cart[0].Entry.UnitWeightT); Assert.Equal(model.Result.KnownNetPln, model.Cart[0].Entry.UnitKnownNetPln);
        model.DimensionA = "8"; Assert.Null(model.Result); Assert.False(model.CanAddResult); Assert.True(model.Dirty);
    }
    [Fact]
    public async Task Missing_arch_price_and_legacy_warning_survive_cart_save_while_zero_quantity_cannot_be_added()
    {
        var server = new NativeLegoServer(); using var api = await Api(server); var store = new MemoryLegoCartStore(); var model = new LegoViewModel(api, store, 101);
        await model.LoadCatalog(default); await model.LoadCart(default); model.SeriesKey = "lego60std"; model.Boxes = "2"; model.WithArch = true;
        Assert.True(await model.Calculate(default)); Assert.True(model.Result!.MissingArchPrice); Assert.Contains("Brak ceny łuków", model.Warnings); Assert.True(model.CanAddResult); model.AddResult();
        Assert.False(model.Cart[0].Entry.Complete); Assert.Contains(model.Cart[0].Entry.Warnings, message => message.Contains("Łuk wymaga")); Assert.True(await model.SaveCart(default));
        var reloaded = new LegoViewModel(api, store, 101); await reloaded.LoadCart(default); Assert.Equal(model.Cart[0].Warning, reloaded.Cart[0].Warning); Assert.False(reloaded.Cart[0].Entry.Complete);
        model.SeriesKey = "lego4090"; model.Shape = "prosta"; model.DimensionA = "0,8"; model.Height = "0,4";
        Assert.True(await model.Calculate(default)); Assert.Equal(0, model.Result!.Quantity); Assert.False(model.CanAddResult); Assert.NotEmpty(model.Warnings);
    }
    [Fact]
    public async Task Calculation_connection_error_cancel_and_401_keep_fields_for_retry()
    {
        var server = new NativeLegoServer { LoseNext = true }; using var api = await Api(server); var model = new LegoViewModel(api, new MemoryLegoCartStore(), 101); await model.LoadCatalog(default);
        model.SeriesKey = "lego60std"; model.Shape = "prosta"; model.DimensionA = "7,2"; model.Height = "1,2";
        Assert.False(await model.Calculate(default)); Assert.Equal("7,2", model.DimensionA); Assert.Null(model.Result); Assert.True(await model.Calculate(default));
        server.ExpireNext = true; Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Calculate(default))).Status); Assert.Equal("7,2", model.DimensionA); Assert.False(model.IsCalculating);
        await api.Login("biuro", "0000", default); Assert.True(await model.Calculate(default));
        using var cancellation = new CancellationTokenSource(); server.NextCalculation = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calculation = model.Calculate(cancellation.Token); cancellation.Cancel(); Assert.False(await calculation); Assert.Equal("7,2", model.DimensionA); Assert.False(model.IsCalculating);
    }
    [Fact]
    public async Task Quantity_limits_and_failed_local_write_retain_cart_and_retry_without_double_count()
    {
        var server = new NativeLegoServer(); using var api = await Api(server); var store = new MemoryLegoCartStore { FailNextWrite = true }; var model = new LegoViewModel(api, store, 101);
        await model.LoadCatalog(default); await model.LoadCart(default); model.SelectedProduct = model.Products.Single(row => row.Key == "rainTank"); model.AddProduct(); model.AddProduct();
        var row = Assert.Single(model.Cart); Assert.Equal(2, row.Quantity); row.QuantityText = "1000"; Assert.False(model.CartValid); Assert.False(model.CanSaveCart); Assert.Contains("Popraw ilości", model.CartSummary);
        row.QuantityText = "3"; Assert.Contains("27,00", model.CartSummary.Replace("27.00", "27,00")); Assert.False(await model.SaveCart(default)); Assert.True(model.CartDirty); Assert.Single(model.Cart);
        Assert.True(await model.SaveCart(default)); Assert.False(model.CartDirty); Assert.Equal(3, Assert.Single(store.Users[101]).Quantity);
        model.SelectedCartRow = row; model.Remove(); Assert.Empty(model.Cart); Assert.True(model.CartDirty);
    }
    [Theory]
    [InlineData("ceo")] [InlineData("produkcja")] [InlineData("crm")]
    public async Task Roles_outside_calculator_cannot_load_calculate_or_edit_cart(string role)
    {
        var server = new NativeLegoServer { Role = role }; using var api = await Api(server); var model = new LegoViewModel(api, new MemoryLegoCartStore(), 101);
        await model.LoadCatalog(default); await model.LoadCart(default); Assert.False(await model.Calculate(default)); Assert.False(model.CanEdit); Assert.Empty(server.CalculationBodies);
        using var main = new MainViewModel(api) { Section = MainSection.Lego }; Assert.False(main.IsLegoVisible);
    }
    [Fact]
    public async Task Real_atomic_store_is_bounded_preserves_previous_file_and_isolates_accounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); var store = new LegoCartStore(directory);
        var entry = new LegoCartEntry(Guid.NewGuid(), "roadPlate", "Synthetic product", "Synthetic dimensions", 1.5m, 620, true, [], 2);
        try
        {
            await store.Write(101, [entry], default); Assert.Single(await store.Read(101, default)); Assert.Empty(await store.Read(202, default));
            var bad = entry with { Quantity = 1000 }; await Assert.ThrowsAsync<InvalidDataException>(() => store.Write(101, [bad], default)); Assert.Equal(2, Assert.Single(await store.Read(101, default)).Quantity);
            var many = Enumerable.Range(0, 101).Select(_ => entry with { Id = Guid.NewGuid() }).ToArray(); await Assert.ThrowsAsync<InvalidDataException>(() => store.Write(101, many, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Write(101, [entry with { Quantity = 3 }], cancelled.Token));
            Assert.Equal(2, Assert.Single(await store.Read(101, default)).Quantity); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            await File.WriteAllTextAsync(Path.Combine(directory, "user-202.json"), "[{\"Id\":null}]"); await Assert.ThrowsAnyAsync<Exception>(() => store.Read(202, default)); Assert.Single(await store.Read(101, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Different_session_cannot_save_or_view_previous_model_as_an_active_cart()
    {
        var server = new NativeLegoServer(); using var api = await Api(server); var store = new MemoryLegoCartStore(); var old = new LegoViewModel(api, store, 101); await old.LoadCatalog(default); await old.LoadCart(default); old.AddProduct();
        api.Logout(); server.Actor = 202; await api.Login("biuro", "0000", default); old.Notify(); Assert.False(old.Allowed); Assert.False(old.CanCartEdit); Assert.False(await old.SaveCart(default));
        var current = new LegoViewModel(api, store, 202); await current.LoadCart(default); Assert.Empty(current.Cart); Assert.Single(old.Cart); Assert.True(old.CartDirty);
    }
    internal static async Task<CrmClient> Api(NativeLegoServer server) { var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(server.Role, "0000", default); return api; }
}

internal sealed class MemoryLegoCartStore : ILegoCartStore
{
    public Dictionary<long, LegoCartEntry[]> Users { get; } = [];
    public bool FailNextWrite { get; set; }
    public Task<LegoCartEntry[]> Read(long userId, CancellationToken ct) => Task.FromResult(Users.TryGetValue(userId, out var rows) ? rows.ToArray() : []);
    public Task Write(long userId, LegoCartEntry[] entries, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (FailNextWrite) { FailNextWrite = false; throw new IOException("Synthetic full disk"); } Users[userId] = entries.ToArray(); return Task.CompletedTask; }
}
internal sealed class NativeLegoServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Role { get; set; } = "biuro";
    public long Actor { get; set; } = 101;
    public bool LoseNext { get; set; }
    public bool ExpireNext { get; set; }
    public LegoInput? Input { get; private set; }
    public List<string> CalculationBodies { get; } = [];
    public TaskCompletionSource<HttpResponseMessage>? NextCalculation { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/shift-reports/features") return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures);
        if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-lego" });
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(Actor, "Synthetic LEGO user", null, null, Role));
        if (path.EndsWith("/orders/features")) return EditorTests.Reply(new OrderFeatures(false));
        if (path.EndsWith("/lego/catalog")) return EditorTests.Reply(new LegoCatalog(LegoCalculator.Series, LegoCalculator.Products));
        if (path.EndsWith("/lego") && request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(ct); CalculationBodies.Add(body); Input = JsonSerializer.Deserialize<LegoInput>(body, Json)!;
            if (ExpireNext) { ExpireNext = false; return EditorTests.Reply(new ApiError(401, "Synthetic expired session"), HttpStatusCode.Unauthorized); }
            if (LoseNext) { LoseNext = false; throw new HttpRequestException("Synthetic connection lost"); }
            if (NextCalculation is { } delayed) { NextCalculation = null; return await delayed.Task.WaitAsync(ct); }
            try { return EditorTests.Reply(LegoCalculator.Calculate(Input, ct)); }
            catch (ArgumentException ex) { return EditorTests.Reply(new ApiError(422, ex.Message), HttpStatusCode.UnprocessableEntity); }
        }
        return EditorTests.Reply(new ApiError(404, "Synthetic unavailable module"), HttpStatusCode.NotFound);
    }
}
