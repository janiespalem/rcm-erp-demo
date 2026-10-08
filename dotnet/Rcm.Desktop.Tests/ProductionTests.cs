using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class ProductionTests
{
    [Theory]
    [InlineData("", null)] [InlineData("0", 0L)] [InlineData("1,234", 1234L)] [InlineData("1.001", 1001L)] [InlineData("1000000000", 1000000000000L)]
    public void Kilograms_convert_exactly_to_nullable_integer_grams(string value, long? expected)
    { Assert.True(ProductionMass.TryGrams(value, out var result)); Assert.Equal(expected, result); }
    [Theory]
    [InlineData("1.0000")] [InlineData("-1")] [InlineData("1e3")] [InlineData("1 000")] [InlineData("1,2.3")] [InlineData("1000000000.001")] [InlineData("1.")] [InlineData("NaN")]
    public void Invalid_or_excess_precision_mass_is_rejected_without_rounding(string value)
    { Assert.False(ProductionMass.TryGrams(value, out _)); }
    [Theory]
    [InlineData("create-contract")] [InlineData("edit-contract")] [InlineData("create-delivery")] [InlineData("correct-delivery")]
    public async Task Lost_reply_replays_identical_contract_or_delivery_command(string action)
    {
        var server = new ProductionServer { LoseNext = true }; using var api = await Login(server);
        var delivery = action.Contains("delivery"); var create = action.StartsWith("create");
        var model = new ProductionEditorViewModel(server.Features, delivery || !create ? server.Contract : null, action == "correct-delivery" ? server.Delivery : null, delivery); model.RefreshAccess(api);
        if (!create) model.Reason = "Synthetic confirmed adjustment";
        if (!delivery) model.Field("name").Value = "Synthetic new contract";
        model.Field("d6").Value = "3,768"; if (!delivery) model.OpeningDate = model.Today;
        Assert.False(await model.Save(api, default)); Assert.True(model.Uncertain); Assert.True(model.Dirty); Assert.False(model.CanEdit); Assert.Equal("3,768", model.Field("d6").Value);
        Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Single(server.Receipts); Assert.False(model.Dirty); Assert.False(model.Uncertain);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unchanged_contract_save_confirms_same_version_including_lost_response(bool lostResponse)
    {
        var server = new ProductionServer { LoseNext = lostResponse }; using var api = await Login(server);
        var model = new ProductionEditorViewModel(server.Features, server.Contract); model.RefreshAccess(api);
        model.Field("name").Value = "  " + server.Contract.Fields.Name + "  "; model.Reason = "Synthetic explanatory note";
        Assert.True(model.CanSave);
        var saved = await model.Save(api, default);
        if (lostResponse) { Assert.False(saved); Assert.True(model.Uncertain); Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); }
        else Assert.True(saved);
        Assert.Equal(1, model.Contract!.Version); Assert.Equal(server.Contract.Fields.Name, model.Field("name").Value);
        Assert.True(model.Committed); Assert.False(model.Uncertain); Assert.False(model.Dirty); Assert.Empty(model.Reason); Assert.Single(server.Receipts);
    }
    [Theory]
    [InlineData(422)] [InlineData(409)] [InlineData(401)]
    public async Task Rejection_retains_draft_and_401_requires_same_identity(int status)
    {
        var server = new ProductionServer { RejectNext = status }; using var api = await Login(server);
        var model = new ProductionEditorViewModel(server.Features, server.Contract); model.RefreshAccess(api); model.Field("name").Value = "Synthetic retained draft";
        if (status == 401)
        {
            await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, default)); Assert.True(model.Uncertain);
            server.UserId = 202; await Assert.ThrowsAsync<ApiFailure>(() => api.Login("biuro", "0000", default)); Assert.Equal(101, api.Session!.UserId);
            server.UserId = 101; await api.Login("biuro", "0000", default); Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]);
        }
        else { Assert.False(await model.Save(api, default)); Assert.Equal("Synthetic retained draft", model.Field("name").Value); Assert.True(model.Dirty); Assert.Equal(status == 409, model.HasConflict); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Conflict_compares_authoritative_versions_and_requires_explicit_draft_save(bool delivery)
    {
        var server = new ProductionServer(); using var api = await Login(server);
        var model = new ProductionEditorViewModel(server.Features, server.Contract, delivery ? server.Delivery : null, delivery); model.RefreshAccess(api);
        if (delivery) { model.Field("d6").Value = "4,001"; model.Reason = "Synthetic recount"; } else model.Field("name").Value = "Synthetic local name";
        server.Contract = server.Contract with { Version = 2, Fields = server.Contract.Fields with { Name = "Synthetic changed remotely" } };
        if (delivery) server.Delivery = server.Delivery with { Version = 2 };
        Assert.False(await model.Save(api, default)); Assert.True(model.HasConflict); Assert.False(model.CanSave);
        var beforeComparison = server.Paths.Count; await model.Compare(api, default); Assert.NotEmpty(model.Comparison); Assert.True(model.CanAcceptComparison); Assert.Single(server.Bodies);
        Assert.Equal(delivery ? 2 : 1, server.Paths.Count - beforeComparison);
        if (delivery) { Assert.Equal($"/api/v1/production/contracts/{server.Contract.Id}/deliveries/{server.Delivery.Id}", server.Paths[^1]); Assert.DoesNotContain(server.Paths.Skip(beforeComparison), path => path.Contains("page=")); }
        model.AcceptComparison(); Assert.Equal(2, model.Contract!.Version); Assert.True(model.CanSave); Assert.Single(server.Bodies);
        Assert.Equal(delivery ? "4,001" : "Synthetic local name", model.Field(delivery ? "d6" : "name").Value);
        Assert.True(await model.Save(api, default)); Assert.NotEqual(server.Bodies[0], server.Bodies[1]); Assert.Equal(3, model.Contract.Version);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Delivery_comparison_rejects_wrong_identity_and_retains_local_draft(bool wrongContract)
    {
        var server = new ProductionServer(); using var api = await Login(server);
        var model = new ProductionEditorViewModel(server.Features, server.Contract, server.Delivery, true); model.RefreshAccess(api);
        model.Field("d6").Value = "4,001"; model.Reason = "Synthetic recount"; server.Contract = server.Contract with { Version = 2 };
        Assert.False(await model.Save(api, default)); Assert.True(model.HasConflict);
        server.DeliveryLookupOverride = wrongContract ? server.Delivery with { ContractId = Guid.NewGuid() } : server.Delivery with { Id = Guid.NewGuid() };
        var error = await Assert.ThrowsAsync<ApiFailure>(() => model.Compare(api, default)); Assert.Equal(502, error.Status);
        Assert.False(model.CanAcceptComparison); Assert.True(model.HasConflict); Assert.True(model.Dirty);
        Assert.Equal("4,001", model.Field("d6").Value); Assert.Equal("Synthetic recount", model.Reason); Assert.Single(server.Bodies);
    }
    [Theory]
    [InlineData("biuro", true)] [InlineData("technolog", true)] [InlineData("ceo", false)] [InlineData("produkcja", false)]
    public async Task Readers_cannot_mutate_even_when_server_write_flag_is_incorrect(string role, bool writes)
    {
        var server = new ProductionServer { Role = role }; using var api = await Login(server);
        var model = new ProductionEditorViewModel(server.Features with { Write = true }, server.Contract); model.RefreshAccess(api); model.Field("name").Value = "Synthetic role change";
        Assert.Equal(writes, model.CanSave); Assert.Equal(writes, await model.Save(api, default)); Assert.Equal(writes ? 1 : 0, server.Bodies.Count);
        var denied = new ProductionEditorViewModel(server.Features with { Write = false }, server.Contract); denied.RefreshAccess(api); denied.Field("name").Value = "Synthetic blocked"; Assert.False(await denied.Save(api, default));
    }
    [Fact]
    public async Task Dates_use_business_today_and_invalid_mass_never_reaches_server()
    {
        var server = new ProductionServer(); using var api = await Login(server); var model = new ProductionEditorViewModel(server.Features, server.Contract, deliveryMode: true); model.RefreshAccess(api);
        Assert.Equal(new DateTime(2026, 10, 1), model.DeliveryDate); model.Field("d6").Value = "0.0001";
        Assert.False(await model.Save(api, default)); Assert.NotEmpty(model.Errors); Assert.Empty(server.Bodies); Assert.Equal("0.0001", model.Field("d6").Value);
        model.Field("d6").Value = "0"; model.DeliveryDate = model.Today.AddDays(1); Assert.False(await model.Save(api, default)); Assert.Empty(server.Bodies);
    }
    [Fact]
    public async Task Newer_search_cannot_be_overwritten_by_late_response_and_pagination_is_explicit()
    {
        var server = new ProductionServer(); using var api = await Login(server); var model = new ProductionViewModel(api); model.SetFeatures(server.Features);
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); server.DelayList = delayed;
        var old = model.Load(default); model.Query = "new filter"; model.Page = 2; await model.Load(default);
        delayed.SetResult(EditorTests.Reply(new Page<ProductionContractDto>([server.Contract with { Fields = server.Contract.Fields with { Name = "stale" } }], 51, 1, 50))); await old;
        Assert.Equal(server.Contract.Fields.Name, Assert.Single(model.Rows).Name); Assert.Equal(2, model.Page); Assert.Contains("q=new%20filter&page=2&pageSize=50", server.Paths[^1]);
        await model.Open(server.Contract.Id, default); Assert.Contains("bez odliczenia", model.Material); Assert.Contains("nie oznaczają stanu magazynu", model.Warning); Assert.True(Assert.Single(model.Deliveries).Delivery.BeforeOpening);
    }
    [Theory]
    [InlineData(1, "05.01.2026 13:00")] [InlineData(7, "05.07.2026 14:00")]
    public void Audit_time_uses_Warsaw_with_winter_and_summer_offsets(int month, string expected)
    {
        var row = new ProductionAuditRow(new(Guid.NewGuid(), 101, "contract_changed", "", "{}", new(2026, month, 5, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(expected, row.RecordedAt); Assert.Equal("Zmiana kontraktu", row.Action);
    }
    [Fact]
    public void Audit_contract_creation_shows_synthetic_status_norm_dates_and_nullable_masses()
    {
        var fields = new ProductionContractFields("Synthetic contract", "Synthetic reference", 100, new(2026, 11, 1), new(2026, 10, 1), new(0, null, 49056), false);
        var json = JsonSerializer.Serialize(new { fields, norm = new ProductionNorm(4, new(3768, 3268, 49056)), isSynthetic = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var row = new ProductionAuditRow(new(Guid.NewGuid(), 101, "contract_created", "", json, DateTimeOffset.UtcNow));
        Assert.Equal("Utworzenie kontraktu", row.Action); Assert.Contains("Kontrakt testowy", row.Changes); Assert.Contains("Norma: 4 kosze / wyrób", row.Changes);
        Assert.Contains("Nazwa: Synthetic contract", row.Changes); Assert.Contains("Termin: 01.11.2026", row.Changes); Assert.Contains("Data otwarcia: 01.10.2026", row.Changes);
        Assert.Contains("Ø6: 0 kg", row.Changes); Assert.Contains("Ø12: nieznana", row.Changes); Assert.Contains("Ø16: 49,056 kg", row.Changes); Assert.Contains("Norma potwierdzona: nie", row.Changes);
        Assert.DoesNotContain("{", row.Changes); Assert.DoesNotContain("diameter", row.Changes);
    }
    [Fact]
    public void Audit_changes_show_readable_before_and_after_for_mass_date_and_norm()
    {
        var json = JsonSerializer.Serialize(new
        {
            openingSteel = new { before = new SteelMasses(0, null, 1000), after = new SteelMasses(1001, 0, 2000) },
            openingDate = new { before = "2026-09-30", after = "2026-10-01" }, normConfirmed = new { before = false, after = true }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var row = new ProductionAuditRow(new(Guid.NewGuid(), 101, "contract_changed", "Synthetic confirmation", json, DateTimeOffset.UtcNow));
        Assert.Contains("Data otwarcia: 30.09.2026 → 01.10.2026", row.Changes); Assert.Contains("Norma potwierdzona: nie → tak", row.Changes);
        Assert.Contains("Stal początkowa: Ø6: 0 kg", row.Changes); Assert.Contains("→ Ø6: 1,001 kg", row.Changes); Assert.Contains("Ø12: nieznana", row.Changes);
        Assert.DoesNotContain("before", row.Changes); Assert.DoesNotContain("after", row.Changes);
    }
    [Fact]
    public async Task Audit_delivery_correction_formats_nested_changes_in_loaded_page()
    {
        var server = new ProductionServer(); using var api = await Login(server); var model = new ProductionViewModel(api); model.SetFeatures(server.Features);
        await model.Open(server.Contract.Id, default); await model.LoadAudit(default);
        var row = Assert.Single(model.Audit); Assert.Equal("Korekta dostawy", row.Action); Assert.Contains("Stal w dostawie: Ø6: 3 kg", row.Changes); Assert.Contains("→ Ø6: 4 kg", row.Changes);
        Assert.Contains("Synthetic recount", row.Reason); Assert.DoesNotContain("deliveryId", row.Changes); Assert.Contains("page=1&pageSize=50", server.Paths[^1]);
        var creation = new ProductionAuditRow(new(Guid.NewGuid(), 101, "delivery_created", "", JsonSerializer.Serialize(new { deliveryId = server.Delivery.Id, fields = server.Delivery.Fields }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow));
        Assert.Equal("Dodanie dostawy", creation.Action); Assert.Contains("Data dostawy: 30.09.2026", creation.Changes); Assert.Contains("Uwagi: Synthetic historic steel", creation.Changes);
    }
    [Fact]
    public void Malformed_audit_never_exposes_technical_payload()
    {
        var row = new ProductionAuditRow(new(Guid.NewGuid(), 101, "unexpected_action", "", "{malformed", DateTimeOffset.UtcNow));
        Assert.Equal("Zmiana danych", row.Action); Assert.Equal("Szczegóły zmiany niedostępne.", row.Changes);
    }
    internal static async Task<CrmClient> Login(ProductionServer server)
    { var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(server.Role, "0000", default); return api; }
}

internal sealed class ProductionServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Role { get; set; } = "biuro";
    public long UserId { get; set; } = 101;
    public bool LoseNext { get; set; }
    public int RejectNext { get; set; }
    public ProductionFeatures Features => new(true, Role is "biuro" or "technolog", new(2026, 10, 1));
    public ProductionContractDto Contract { get; set; } = new(Guid.NewGuid(), 1, new("Synthetic production contract", OpeningDate: new(2026, 10, 1), OpeningSteel: new(376800, 326800, 4905600), NormConfirmed: true), new(4, new(3768, 3268, 49056)), new(new(376800, 326800, 4905600), 100, 400, [6, 12, 16], new(0, 0, 0)), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    public SteelDeliveryDto Delivery { get; set; }
    public SteelDeliveryDto? DeliveryLookupOverride { get; set; }
    public Dictionary<Guid, object> Receipts { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> Paths { get; } = [];
    public TaskCompletionSource<HttpResponseMessage>? DelayList { get; set; }
    public ProductionServer() => Delivery = new(Guid.NewGuid(), Contract.Id, 1, new(new(2026, 9, 30), new(0, null, 49056), "Synthetic historic steel"), 101, DateTimeOffset.UtcNow, 101, DateTimeOffset.UtcNow, true);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath; Paths.Add(request.RequestUri.PathAndQuery);
        if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-production" });
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(UserId, "Synthetic production user", null, null, Role, ProductionEnabled: true));
        if (path.EndsWith("/orders/features")) return EditorTests.Reply(new OrderFeatures(false));
        if (path.EndsWith("/production/review/features")) return EditorTests.Reply(new ProductionReviewFeatures(false, false, false));
        if (path.EndsWith("/production/features")) return EditorTests.Reply(Features);
        if (request.Method == HttpMethod.Get)
        {
            if (path.EndsWith("/audit")) return EditorTests.Reply(new Page<ProductionChangeDto>([new(Guid.NewGuid(), 101, "delivery_corrected", "Synthetic recount", JsonSerializer.Serialize(new { deliveryId = Delivery.Id, changes = new { steel = new { before = new SteelMasses(3000, 0, 0), after = new SteelMasses(4000, 0, 0) } } }, Json), DateTimeOffset.UtcNow)], 1, 1, 50));
            if (path.Contains("/deliveries/")) return EditorTests.Reply(DeliveryLookupOverride ?? Delivery);
            if (path.EndsWith("/deliveries")) return EditorTests.Reply(new Page<SteelDeliveryDto>([Delivery], 1, 1, 50));
            if (path.EndsWith("/" + Contract.Id)) return EditorTests.Reply(Contract);
            if (DelayList is { } delayed) { DelayList = null; return await delayed.Task; }
            return EditorTests.Reply(new Page<ProductionContractDto>([Contract], 51, request.RequestUri.Query.Contains("page=2") ? 2 : 1, 50));
        }
        var body = await request.Content!.ReadAsStringAsync(ct); Bodies.Add(body); var input = JsonSerializer.Deserialize<JsonElement>(body); var id = input.GetProperty("requestId").GetGuid();
        if (RejectNext != 0) { var status = RejectNext; RejectNext = 0; return EditorTests.Reply(new ApiError(status, "Synthetic rejected write", new() { ["fields.name"] = ["Synthetic validation error"] }), (HttpStatusCode)status); }
        if (!Receipts.TryGetValue(id, out var result))
        {
            if ((input.TryGetProperty("expectedContractVersion", out var cv) && cv.GetInt64() != Contract.Version) || (input.TryGetProperty("expectedVersion", out var v) && v.GetInt64() != (path.Contains("/deliveries/") ? Delivery.Version : Contract.Version))) return EditorTests.Reply(new ApiError(409, "Synthetic version conflict"), HttpStatusCode.Conflict);
            if (path.Contains("/deliveries"))
            {
                var fields = input.GetProperty("fields").Deserialize<SteelDeliveryFields>(Json)!; var correction = path.Contains("/deliveries/");
                Delivery = Delivery with { Id = correction ? Delivery.Id : Guid.NewGuid(), Version = correction ? Delivery.Version + 1 : 1, Fields = fields, BeforeOpening = fields.DeliveryDate < Contract.Fields.OpeningDate };
                Contract = Contract with { Version = Contract.Version + 1 }; result = new SteelDeliveryResult(Delivery, Contract.Version);
            }
            else
            {
                var fields = input.GetProperty("fields").Deserialize<ProductionContractFields>(Json)!; var create = request.Method == HttpMethod.Post;
                Contract = Contract with { Id = create ? Guid.NewGuid() : Contract.Id, Version = create ? 1 : Contract.Version + (fields == Contract.Fields ? 0 : 1), Fields = fields }; result = Contract;
            }
            Receipts.Add(id, result);
        }
        if (LoseNext) { LoseNext = false; throw new HttpRequestException("Synthetic response lost after commit"); }
        return EditorTests.Reply(result);
    }
}
