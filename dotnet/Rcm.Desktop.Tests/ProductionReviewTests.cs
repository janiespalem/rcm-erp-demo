using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class ProductionReviewTests
{
    internal static async Task<CrmClient> Login(ProductionReviewServer server)
    { var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(server.Role, "0000", default); return api; }
    internal static async Task<ShiftReportsEditorViewModel> Editor(ProductionReviewServer server, CrmClient api, bool create = false)
    { var model = new ShiftReportsEditorViewModel(create ? null : server.Source.Report, server.Source.Features); model.RefreshAccess(api); await model.Production.Initialize(api, default); return model; }
    [Fact]
    public async Task New_report_keeps_selection_until_explicit_link_and_does_not_submit()
    {
        var server = new ProductionReviewServer(); using var api = await Login(server); var model = await Editor(server, api, true);
        model.Production.SelectedContract = model.Production.Contracts[0]; Assert.True(model.CanCreate); Assert.False(model.Production.CanLink);
        Assert.True(await model.Create(api, default)); model.Field("remarks").Value = "Typed after draft creation";
        Assert.True(await model.Production.Link(default)); Assert.Equal("Typed after draft creation", model.Field("remarks").Value);
        Assert.Equal(server.Contract.Id, server.Link!.ContractId); Assert.Equal("draft", server.State.State); Assert.Equal("Przekaż do sprawdzenia", model.FinalizeLabel);
        Assert.DoesNotContain(server.Source.Paths, path => path.EndsWith("/finalize")); Assert.False(model.CanFinalize);
    }
    [Fact]
    public async Task Explicit_link_finalize_accept_and_correction_excludes_old_acceptance()
    {
        var server = new ProductionReviewServer(); server.Source.Report = server.Source.Report with { Fields = NativeShiftReportsServer.CompleteFields, ValidationErrors = [] };
        using var worker = await Login(server); var model = await Editor(server, worker);
        Assert.Equal("Zakończ raport", model.FinalizeLabel); model.Production.SelectedContract = model.Production.Contracts[0]; Assert.False(model.CanFinalize);
        Assert.True(await model.Production.Link(default)); Assert.True(model.CanFinalize); Assert.True(await model.FinalizeReport(worker, default)); await model.Production.Refresh(default);
        Assert.Equal("pending", model.Production.State!.State); Assert.False(model.Production.CanReview);
        server.Role = "biuro"; server.Reviewer = true; using var reviewer = await Login(server); var review = await Editor(server, reviewer);
        Assert.False(review.CanEdit); Assert.False(review.Production.CanChoose); Assert.True(review.Production.CanReview); Assert.True(await review.Production.Review("accepted", default));
        var card = new ProductionReportsViewModel(reviewer); await card.Initialize(default); card.SelectContract(server.Contract.Id); await card.Load(default);
        Assert.Equal(10, card.Operations!.Assembled); Assert.Equal(10, card.Operations.Poured); Assert.Equal(1, card.Operations.Reports); Assert.Contains("Formy", card.Forms); Assert.Contains("Rozformowane", card.Products);
        server.Role = "produkcja"; server.Reviewer = false; model.RefreshAccess(worker); model.StartCorrection(); model.Field("poured").Value = "12"; model.CorrectionReason = "Recount";
        Assert.True(await model.Correct(worker, default)); Assert.Contains("Wersja raportu zmieniona", model.Production.StateLabel); await model.Production.Refresh(default);
        Assert.Equal("pending", server.State.State); await card.Load(default); Assert.Equal(0, card.Operations!.Reports); Assert.Equal(0, card.Operations.Poured);
    }
    [Theory]
    [InlineData("link")]
    [InlineData("accepted")]
    [InlineData("returned")]
    public async Task Lost_response_replays_frozen_operation_exactly(string action)
    {
        var server = new ProductionReviewServer(); if (action != "link") server.MakePending(); server.Reviewer = action != "link"; using var api = await Login(server); var model = await Editor(server, api);
        if (action == "link") model.Production.SelectedContract = model.Production.Contracts[0];
        if (action == "link") model.Production.LinkReason = "Contract allocation"; else model.Production.ReviewReason = "Measured discrepancy"; server.LoseNext = true;
        Assert.False(action == "link" ? await model.Production.Link(default) : await model.Production.Review(action, default)); Assert.True(model.Production.Uncertain); Assert.True(model.Dirty); Assert.False(model.CanEdit); Assert.False(model.Production.CanChoose);
        if (action == "link") model.Production.LinkReason = "Changed after lost response"; else model.Production.ReviewReason = "Changed after lost response";
        Assert.True(await model.Production.Retry(default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Single(server.Receipts); Assert.False(model.Dirty);
        Assert.Equal(action == "link" ? "Contract allocation" : "Measured discrepancy", server.LastReason);
    }
    [Theory]
    [InlineData(422)] [InlineData(409)] [InlineData(503)] [InlineData(401)] [InlineData(403)]
    public async Task Link_failures_keep_typed_source_selection_and_reason(int status)
    {
        var server = new ProductionReviewServer(); using var api = await Login(server); var model = await Editor(server, api);
        model.Field("remarks").Value = "Unsaved source fields"; model.Production.SelectedContract = model.Production.Contracts[0]; model.Production.LinkReason = "Typed allocation reason"; server.RejectNext = status;
        if (status == 401) Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Production.Link(default))).Status);
        else Assert.False(await model.Production.Link(default));
        Assert.Equal("Unsaved source fields", model.Field("remarks").Value); Assert.Equal(server.Contract.Id, model.Production.SelectedContract!.Contract.Id); Assert.Equal("Typed allocation reason", model.Production.LinkReason); Assert.True(model.Dirty);
        Assert.Equal(status is 503 or 401, model.Production.Uncertain); Assert.Equal(status == 409, model.Production.HasConflict);
        if (status == 401)
        {
            server.Source.ActorId = 202; Assert.Equal(403, (await Assert.ThrowsAsync<ApiFailure>(() => api.Login("produkcja", "0000", default))).Status);
            server.Source.ActorId = 101; await api.Login("produkcja", "0000", default); model.RefreshAccess(api); Assert.True(await model.Production.Retry(default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Equal("Unsaved source fields", model.Field("remarks").Value);
        }
    }
    [Fact]
    public async Task Review_conflict_compares_actual_fields_and_requires_explicit_new_version()
    {
        var server = new ProductionReviewServer { Reviewer = true }; server.MakePending(); using var api = await Login(server); var model = await Editor(server, api); model.Production.ReviewReason = "Typed review reason";
        server.Source.Report = server.Source.Report with { Version = 3, Fields = server.Source.Report.Fields with { Poured = 27 } };
        Assert.False(await model.Production.Review("accepted", default)); Assert.True(model.Production.HasConflict); Assert.False(model.Production.CanReview);
        await model.Production.Compare(default); Assert.Contains(model.Comparison, change => change.Before == "27" && change.After == "10"); Assert.Equal(2, model.Current!.Version);
        model.Production.AcceptComparison(); Assert.Equal(3, model.Current.Version); Assert.Equal("27", model.Field("poured").Value); Assert.Equal("Typed review reason", model.Production.ReviewReason); Assert.True(model.Production.CanReview);
        Assert.True(await model.Production.Review("accepted", default)); Assert.Equal(3, server.State.CurrentReview!.ReportVersion);
        Assert.NotEqual(JsonDocument.Parse(server.Bodies[0]).RootElement.GetProperty("requestId").GetGuid(), JsonDocument.Parse(server.Bodies[1]).RootElement.GetProperty("requestId").GetGuid());
    }
    [Fact]
    public async Task Relink_conflict_preserves_local_source_fields_and_reason()
    {
        var server = new ProductionReviewServer(); server.MakePending(); using var api = await Login(server); var model = await Editor(server, api);
        model.StartCorrection(); model.Field("remarks").Value = "Unsaved correction"; model.CorrectionReason = "Correction reason";
        server.Contract = server.Contract with { Id = Guid.NewGuid(), Fields = server.Contract.Fields with { Name = "Second explicit contract" } };
        model.Production.SelectedContract = new(server.Contract); Assert.False(await model.Production.Link(default)); Assert.Empty(server.Bodies);
        model.Production.LinkReason = "Actual contract corrected"; server.Link = server.Link! with { Version = 2 };
        Assert.False(await model.Production.Link(default)); await model.Production.Compare(default); model.Production.AcceptComparison();
        Assert.Equal("Unsaved correction", model.Field("remarks").Value); Assert.Equal("Correction reason", model.CorrectionReason); Assert.True(model.Correction); Assert.Equal("Actual contract corrected", model.Production.LinkReason);
        Assert.True(await model.Production.Link(default)); Assert.Equal(server.Contract.Id, server.Link!.ContractId); Assert.Equal(3, server.Link.Version);
    }
    [Theory]
    [InlineData("biuro", 101, false, false, false)] [InlineData("ceo", 101, false, false, false)]
    [InlineData("produkcja", 202, false, false, false)] [InlineData("technolog", 202, false, true, false)]
    [InlineData("biuro", 101, true, false, true)] [InlineData("produkcja", 101, false, true, false)]
    public async Task Explicit_assignment_source_ownership_and_module_read_are_independent(string role, long actor, bool reviewer, bool link, bool review)
    {
        var server = new ProductionReviewServer { Role = role, Reviewer = reviewer }; server.Source.ActorId = actor; server.MakePending(); using var api = await Login(server); var model = await Editor(server, api);
        Assert.Equal(link, model.Production.CanChoose); Assert.Equal(review, model.Production.CanReview); Assert.False(model.CanSave);
        if (!link) { model.Production.SelectedContract = new(server.Contract with { Id = Guid.NewGuid() }); Assert.False(await model.Production.Link(default)); }
        if (!review) Assert.False(await model.Production.Review("accepted", default)); Assert.Empty(server.Bodies);
        var page = new ProductionReportsViewModel(api); await page.Initialize(default); await page.LoadQueue(default); Assert.Equal(reviewer, page.CanReview); Assert.Equal(reviewer ? 1 : 0, page.Queue.Count);
    }
    [Fact]
    public async Task Stale_opened_report_requires_comparison_and_new_read_cannot_enable_stale_submit()
    {
        var server = new ProductionReviewServer { Reviewer = true }; server.MakePending(); using var api = await Login(server);
        var model = new ShiftReportsEditorViewModel(server.Source.Report, server.Source.Features); model.RefreshAccess(api);
        server.Source.Report = server.Source.Report with { Version = 3, Fields = server.Source.Report.Fields with { Poured = 23 } };
        await model.Production.Initialize(api, default); Assert.False(model.Production.CanReview); Assert.True(model.Production.CanCompare); Assert.Equal("10", model.Field("poured").Value);
        await model.Production.Compare(default); model.Production.AcceptComparison(); Assert.True(model.Production.CanReview); Assert.Equal("23", model.Field("poured").Value);
    }
    [Fact]
    public async Task Failed_initial_state_read_keeps_submit_blocked_until_retry_read_succeeds()
    {
        var server = new ProductionReviewServer(); using var api = await Login(server); var model = new ShiftReportsEditorViewModel(server.Source.Report, server.Source.Features); model.RefreshAccess(api);
        server.FailStateRead = true; Assert.Equal(503, (await Assert.ThrowsAsync<ApiFailure>(() => model.Production.Initialize(api, default))).Status);
        Assert.False(model.CanFinalize); Assert.False(model.CanEdit); Assert.True(model.Production.CanRefresh);
        server.FailStateRead = false; await model.Production.Initialize(api, default); Assert.True(model.CanFinalize); Assert.True(model.CanEdit);
    }
    [Fact]
    public async Task Link_success_does_not_discard_unsubmitted_review_reason()
    {
        var server = new ProductionReviewServer { Reviewer = true }; using var api = await Login(server); var model = await Editor(server, api);
        model.Production.SelectedContract = model.Production.Contracts[0]; model.Production.ReviewReason = "Unsubmitted reviewer notes";
        Assert.True(await model.Production.Link(default)); Assert.Equal("Unsubmitted reviewer notes", model.Production.ReviewReason); Assert.True(model.Dirty);
    }
    [Fact]
    public async Task Malformed_confirmation_is_uncertain_and_retries_the_same_link()
    {
        var server = new ProductionReviewServer { WrongNextReply = true }; using var api = await Login(server); var model = await Editor(server, api);
        model.Production.SelectedContract = model.Production.Contracts[0]; Assert.False(await model.Production.Link(default)); Assert.True(model.Production.Uncertain);
        Assert.True(await model.Production.Retry(default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Single(server.Receipts);
    }
    [Fact]
    public async Task Chooser_pagination_keeps_explicit_selected_contract_in_choices()
    {
        var server = new ProductionReviewServer(); using var api = await Login(server); var model = await Editor(server, api);
        var selected = model.Production.Contracts[0]; model.Production.SelectedContract = selected; model.Production.LinkReason = "Explicit selection";
        server.Contract = server.Contract with { Id = Guid.NewGuid(), Fields = server.Contract.Fields with { Name = "Other page" } }; model.Production.ContractPage = 2; await model.Production.LoadContracts(default);
        Assert.Same(selected, model.Production.SelectedContract); Assert.Contains(selected, model.Production.Contracts); Assert.Equal("Explicit selection", model.Production.LinkReason); Assert.True(model.Production.SelectionDirty);
    }
    [Fact]
    public async Task Unsupported_form_schema_cannot_be_accepted_or_returned()
    {
        var server = new ProductionReviewServer { Reviewer = true }; server.MakePending(); server.Source.Report = server.Source.Report with { SchemaVersion = 2 };
        using var api = await Login(server); var model = await Editor(server, api); Assert.False(model.SchemaKnown); Assert.False(model.Production.CanReview);
        model.Production.ReviewReason = "Unknown form"; Assert.False(await model.Production.Review("accepted", default)); Assert.False(await model.Production.Review("returned", default)); Assert.Empty(server.Bodies);
    }
    [Fact]
    public async Task Returned_report_requires_reason_and_source_correction_before_new_review()
    {
        var server = new ProductionReviewServer { Reviewer = true }; server.MakePending(); using var api = await Login(server); var model = await Editor(server, api);
        Assert.False(await model.Production.Review("returned", default)); Assert.Empty(server.Bodies); model.Production.ReviewReason = "Wrong poured quantity";
        Assert.True(await model.Production.Review("returned", default)); Assert.Equal("returned", server.State.State); Assert.False(model.Production.CanReview); Assert.False(await model.Production.Review("accepted", default)); Assert.Single(server.Bodies);
    }
    [Fact]
    public async Task List_filter_pagination_and_audit_show_business_values()
    {
        var server = new ProductionReviewServer { Reviewer = true }; server.MakePending(); using var api = await Login(server); var page = new ProductionReportsViewModel(api); await page.Initialize(default); page.SelectContract(server.Contract.Id);
        page.Page = 3; page.Filter = ProductionReportsViewModel.Filters.Single(row => row.Key == "pending"); Assert.Equal(1, page.Page); page.Page = 2; await page.Load(default); Assert.Contains("state=pending&page=2&pageSize=50", server.Paths[^2]); Assert.Equal(2, page.Page);
        var model = await Editor(server, api); await model.Production.ReadAudit(default); var row = Assert.Single(model.Production.Audit); Assert.Equal("Zmiana kontraktu", row.Action); Assert.Equal("Kontrakt: Pierwszy → Drugi", row.Changes); Assert.Equal("05.10.2026 14:00", row.Time); Assert.DoesNotContain("{", row.Changes);
    }
}

internal sealed class ProductionReviewServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public NativeShiftReportsServer Source { get; } = new();
    private readonly HttpMessageInvoker source;
    public ProductionReviewServer() { source = new(Source, false); }
    public string Role { get => Source.Role; set => Source.Role = value; }
    public bool Reviewer { get; set; }
    public bool LoseNext { get; set; }
    public bool FailStateRead { get; set; }
    public bool WrongNextReply { get; set; }
    public int RejectNext { get; set; }
    public ProductionContractDto Contract { get; set; } = new ProductionServer().Contract;
    public ProductionReportLinkDto? Link { get; set; }
    public ProductionReportReviewDto? Review { get; set; }
    public string LastReason { get; private set; } = "";
    public List<string> Bodies { get; } = [];
    public List<string> Paths { get; } = [];
    public Dictionary<Guid, ProductionReportReviewState> Receipts { get; } = [];
    public ProductionReportReviewState State
    {
        get
        {
            var current = Review is not null && Review.ReportVersion == Source.Report.Version && Review.LinkVersion == Link?.Version && Review.ContractId == Link.ContractId ? Review : null;
            return new(Source.Report, Link, current, Link is null ? "unassigned" : Source.Report.Status == "draft" ? "draft" : current?.Decision ?? "pending");
        }
    }
    public void MakePending()
    {
        Source.Report = Source.Report with { Fields = NativeShiftReportsServer.CompleteFields, Version = 2, Status = "finalized", ValidationErrors = [] };
        Link = new(Source.Report.Id, 1, Contract.Id, Contract.Fields.Name, 1, 101, DateTimeOffset.UtcNow);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath; Paths.Add(request.RequestUri.PathAndQuery);
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(Source.ActorId, "Synthetic explicitly assigned reviewer", null, null, Role, ProductionEnabled: true));
        if (!path.Contains("/production/")) return await source.SendAsync(request, ct);
        if (path.EndsWith("/review/features")) return EditorTests.Reply(new ProductionReviewFeatures(true, Source.Features.Write, Reviewer));
        if (request.Method == HttpMethod.Get)
        {
            if (path.EndsWith("/review/queue")) return Reviewer ? EditorTests.Reply(new Page<ProductionReportReviewState>(State.State == "pending" ? [State] : [], State.State == "pending" ? 1 : 0, 1, 50)) : Error(403);
            if (path.EndsWith("/accepted-operations")) { var count = State.State == "accepted" ? 1 : 0; return EditorTests.Reply(new ProductionAcceptedOperations(Contract.Id, count, count * 10, count * 10, count * Source.Report.Fields.Poured!.Value, count * 10, count * 10, 0)); }
            if (path.EndsWith("/audit")) return EditorTests.Reply(new Page<ProductionReportAuditDto>([new(Guid.NewGuid(), 1, 101, "report_relinked", "Actual contract corrected", JsonSerializer.SerializeToElement(new { before = new { contractName = "Pierwszy" }, after = new { contractName = "Drugi" } }), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero))], 1, 1, 50));
            if (path.Contains("/contracts/") && path.EndsWith("/reports")) return EditorTests.Reply(new Page<ProductionReportReviewState>([State], 51, request.RequestUri.Query.Contains("page=2") ? 2 : 1, 50));
            if (path.EndsWith("/contracts")) return EditorTests.Reply(new Page<ProductionContractDto>([Contract], 1, 1, 50));
            if (path.Contains("/reports/")) return FailStateRead ? Error(503) : EditorTests.Reply(State);
            if (path.EndsWith("/features")) return EditorTests.Reply(new ProductionFeatures(true, Role is "biuro" or "technolog", new(2026, 10, 5)));
            if (path.EndsWith("/deliveries")) return EditorTests.Reply(new Page<SteelDeliveryDto>([], 0, 1, 50));
            if (path.EndsWith(Contract.Id.ToString())) return EditorTests.Reply(Contract);
            return Error(404);
        }
        var body = await request.Content!.ReadAsStringAsync(ct); Bodies.Add(body); var command = JsonSerializer.Deserialize<JsonElement>(body); var id = command.GetProperty("requestId").GetGuid();
        if (RejectNext != 0) { var status = RejectNext; RejectNext = 0; return Error(status); }
        if (!Receipts.TryGetValue(id, out var result))
        {
            if (command.GetProperty("expectedReportVersion").GetInt64() != Source.Report.Version || command.GetProperty("expectedLinkVersion").GetInt64() != (Link?.Version ?? 0)) return Error(409);
            LastReason = command.GetProperty("reason").GetString()!;
            if (path.EndsWith("/link"))
            {
                if (!(Role == "technolog" || Role == "produkcja" && Source.ActorId == Source.Report.AuthorId)) return Error(403);
                Link = new(1, (Link?.Version ?? 0) + 1, command.GetProperty("contractId").GetGuid(), Contract.Fields.Name, Source.Report.Version, Source.ActorId, DateTimeOffset.UtcNow);
            }
            else
            {
                if (!Reviewer) return Error(403);
                Review = new(Guid.NewGuid(), 1, Source.Report.Version, Link!.Version, Link.ContractId, Source.ActorId, command.GetProperty("decision").GetString()!, LastReason, DateTimeOffset.UtcNow);
            }
            Receipts[id] = result = State;
        }
        if (LoseNext) { LoseNext = false; throw new HttpRequestException("Synthetic lost production review response"); }
        if (WrongNextReply) { WrongNextReply = false; return EditorTests.Reply(result with { Report = result.Report with { Id = 999 } }); }
        return EditorTests.Reply(result);
    }
    private static HttpResponseMessage Error(int status) => EditorTests.Reply(new ApiError(status, "Synthetic retained production error"), (HttpStatusCode)status);
}
