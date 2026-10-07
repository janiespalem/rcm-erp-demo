using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class ShiftReportsTests
{
    [Theory]
    [InlineData("I", "I")]
    [InlineData("II", "II")]
    [InlineData(null, "I")]
    [InlineData("invalid", "I")]
    public async Task Account_shift_defaults_once_and_user_choice_survives_refresh(string? accountShift, string expected)
    {
        var server = new NativeShiftReportsServer { DefaultShift = accountShift }; using var api = await Api(server);
        var model = new ShiftReportsViewModel(api); model.SetFeatures(server.Features);
        Assert.Equal(expected, model.EntryShift);
        model.EntryShift = expected == "I" ? "II" : "I";
        model.SetFeatures(server.Features); await model.Load(default);
        Assert.NotEqual(expected, model.EntryShift);
        model.Clear(); model.SetFeatures(server.Features);
        Assert.Equal(expected, model.EntryShift);
        server.ActorId = 202; server.DefaultShift = "II";
        await api.LogoutAsync(default); await api.Login("produkcja", "0000", default);
        model.SetFeatures(server.Features); Assert.Equal("II", model.EntryShift);
    }
    [Fact]
    public async Task Unfiltered_history_loads_then_date_and_shift_filters_can_be_cleared()
    {
        var server = new NativeShiftReportsServer(); using var api = await Api(server);
        var model = new ShiftReportsViewModel(api); model.SetFeatures(server.Features);
        await model.Load(default); Assert.NotEmpty(model.Rows);
        var path = server.Paths.Single(path => path.StartsWith("/api/v1/shift-reports?"));
        Assert.DoesNotContain("dateFrom",path); Assert.DoesNotContain("dateTo",path); Assert.DoesNotContain("shift=",path);
        model.DateFrom = new(2026,10,1); model.DateTo = new(2026,10,2); model.ShiftFilter = "II";
        await model.Load(default); path=server.Paths.Last(path => path.StartsWith("/api/v1/shift-reports?"));
        Assert.Contains("dateFrom=2026-10-01",path); Assert.Contains("dateTo=2026-10-02",path); Assert.Contains("shift=II",path);
        model.DateFrom = model.DateTo = null; model.ShiftFilter = "";
        await model.Load(default); Assert.NotEmpty(model.Rows);
    }
    [Fact]
    public async Task Lost_draft_save_retries_identical_payload_with_all_fields_and_fixed_questions()
    {
        var server = new NativeShiftReportsServer { LoseNextWrite = true }; using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.Field("reference").Value = "FREE TEXT PART / 2026";
        model.Field("poured").Value = "12"; model.Equipment[1].Note = "Cable 22"; model.Checks[2].Answer = "N/D";
        Assert.False(await model.Save(api, default)); Assert.True(model.Uncertain); Assert.False(model.CanEdit); Assert.True(model.Dirty);
        model.Field("poured").Value = "999";
        Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Single(server.Receipts);
        Assert.Equal(12, server.Report.Fields.Poured); Assert.Equal("FREE TEXT PART / 2026", server.Report.Fields.Reference);
        Assert.Equal("Cable 22", server.Report.Fields.Extensions.Note); Assert.Equal("N/D", server.Report.Fields.Checks[2]); Assert.Equal(9, server.Report.Fields.Checks.Length); Assert.False(model.Dirty);
    }
    [Fact]
    public async Task Finalize_validation_retains_fields_and_complete_draft_cannot_be_discarded()
    {
        var server = new NativeShiftReportsServer { RejectNextWrite = true }; using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.RefreshAccess(api);
        Assert.True(model.CanFinalize); Assert.True(model.CanDiscard);
        Assert.False(await model.FinalizeReport(api, default)); Assert.Equal("Synthetic missing controller", model.Field("controller").Error);
        Assert.Equal(server.Report.Fields.Leader, model.Field("leader").Value); Assert.True(model.CanEdit);
        Complete(model); Assert.True(await model.Save(api, default)); Assert.False(model.CanDiscard); Assert.True(await model.FinalizeReport(api, default));
        Assert.Equal("finalized", model.Current!.Status); Assert.False(model.CanSave); Assert.True(model.CanStartCorrection);
    }
    [Fact]
    public async Task Correction_requires_reason_and_conflict_comparison_keeps_local_values()
    {
        var server = new NativeShiftReportsServer(); server.Report = server.Report with { Status = "finalized", Fields = NativeShiftReportsServer.CompleteFields, ValidationErrors = [] };
        using var api = await Api(server); var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.RefreshAccess(api); model.StartCorrection();
        model.Field("poured").Value = "25"; Assert.False(await model.Correct(api, default)); Assert.Contains("powód", model.ReasonError); Assert.Empty(server.Bodies);
        model.CorrectionReason = "Synthetic recount"; server.Report = server.Report with { Version = 2, Fields = server.Report.Fields with { Poured = 19 } };
        Assert.False(await model.Correct(api, default)); Assert.True(model.HasConflict); Assert.False(model.CanCorrect);
        await model.Compare(api, default); Assert.Contains(model.Comparison, row => row.Before == "19" && row.After == "25"); model.AcceptComparison();
        Assert.Equal("25", model.Field("poured").Value); Assert.Equal("Synthetic recount", model.CorrectionReason); Assert.True(model.Correction);
        server.LoseNextWrite = true; Assert.False(await model.Correct(api, default)); Assert.True(model.Uncertain);
        Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[^1], server.Bodies[^2]); Assert.Equal("corrected", server.Report.Status); Assert.Equal(1, server.Report.CorrectionCount); Assert.False(model.Dirty);
    }
    [Fact]
    public async Task Reauthentication_requires_same_actor_and_deleted_report_retains_draft()
    {
        var server = new NativeShiftReportsServer { ExpireNextWrite = true }; using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.Field("remarks").Value = "Synthetic retained draft";
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, default))).Status); Assert.True(model.Uncertain);
        server.ActorId = 202; Assert.Equal(403, (await Assert.ThrowsAsync<ApiFailure>(() => api.Login("produkcja", "0000", default))).Status); Assert.True(model.Dirty); Assert.True(model.Uncertain);
        server.ActorId = 101; await api.Login("produkcja", "0000", default); Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]);
        model.Field("remarks").Value = "Synthetic local unsaved"; server.RejectStatus = 410;
        Assert.False(await model.Save(api, default)); Assert.Equal("Synthetic local unsaved", model.Field("remarks").Value); Assert.True(model.Dirty); Assert.False(model.CanEdit);
    }
    [Theory]
    [InlineData("produkcja", 202, false, false)] [InlineData("technolog", 202, true, false)] [InlineData("biuro", 101, false, false)] [InlineData("ceo", 101, false, true)]
    public async Task Author_and_admin_gates_are_independent(string role, long actor, bool editable, bool admin)
    {
        var server = new NativeShiftReportsServer { Role = role, ActorId = actor }; using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.RefreshAccess(api);
        Assert.Equal(editable, model.CanEdit); Assert.Equal(role is "technolog" or "ceo", model.CanAdminDelete);
        if (admin) { model.DeleteReason = "Synthetic administrator reason"; Assert.True(await model.AdminDelete(api, default)); Assert.True(model.Removed); }
        else if (!editable) { model.Field("poured").Value = "7"; Assert.False(await model.Save(api, default)); Assert.Empty(server.Bodies); }
        Assert.False(role == "technolog" && actor != server.Report.AuthorId && model.CanDiscard);
    }
    [Fact]
    public async Task Invalid_number_and_native_writer_gate_preserve_input_without_sending_commands()
    {
        var server = new NativeShiftReportsServer(); using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.Field("poured").Value = "1,5";
        Assert.False(await model.Save(api, default)); Assert.Equal("1,5", model.Field("poured").Value); Assert.NotEmpty(model.Field("poured").Error); Assert.Empty(server.Bodies);
        var readOnly = new ShiftReportsEditorViewModel(server.Report, server.Features with { Write = false, CanAdminDelete = false }); readOnly.Field("remarks").Value = "Synthetic";
        Assert.False(await readOnly.Save(api, default)); Assert.Empty(server.Bodies);
    }
    [Fact]
    public async Task Today_totals_use_server_finalized_versions_and_old_page_cannot_overwrite_latest()
    {
        var server = new NativeShiftReportsServer(); using var api = await Api(server); var model = new ShiftReportsViewModel(api); model.SetFeatures(server.Features);
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); server.NextList = delayed;
        var old = model.Load(default); model.Page = 2; model.ShiftFilter = "II"; await model.Load(default);
        delayed.SetResult(EditorTests.Reply(new Page<ShiftReportDto>([server.Report with { AuthorName = "Synthetic stale" }], 1, 1, 50))); await old;
        Assert.Equal(server.Report.AuthorName, Assert.Single(model.Rows).Author); Assert.Equal(2, model.Page); Assert.Contains("Zalane: 17", model.Summary); Assert.Contains("Uszkodzone: 2", model.Summary);
        Assert.Contains("shift=II", server.Paths[^2]);
    }
    [Theory]
    [InlineData("create")]
    [InlineData("discard")]
    [InlineData("delete")]
    public async Task Lost_lifecycle_reply_replays_one_command(string action)
    {
        var server = new NativeShiftReportsServer { LoseNextWrite = true, Role = action == "delete" ? "ceo" : "produkcja" }; using var api = await Api(server);
        var model = new ShiftReportsEditorViewModel(action == "create" ? null : server.Report, server.Features, new DateTime(2026, 10, 1)); model.RefreshAccess(api);
        if (action == "delete") model.DeleteReason = "Synthetic administrator reason";
        var attempt = action switch { "create" => model.Create(api, default), "discard" => model.Discard(api, default), _ => model.AdminDelete(api, default) };
        Assert.False(await attempt); Assert.True(model.Uncertain); Assert.True(model.Dirty);
        Assert.True(await model.Retry(api, default)); Assert.Single(server.Receipts); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.False(model.Dirty);
        Assert.Equal(action != "create", model.Removed);
    }
    [Fact]
    public async Task Audit_shows_typed_before_after_and_draft_checkbox_filter()
    {
        var server = new NativeShiftReportsServer(); using var api = await Api(server); var model = new ShiftReportsEditorViewModel(server.Report, server.Features); model.RefreshAccess(api);
        model.IncludeDrafts = true; await model.ReadAudit(api, default);
        Assert.Contains("includeDrafts=true", server.Paths[^1]); var row = Assert.Single(model.Audit); Assert.Contains("Synthetic recount", row.Reason);
        Assert.Contains(row.Changes, change => change.Label.Contains("zalanych") && change.Before == "10" && change.After == "12");
        Assert.Contains(row.Changes, change => change.Label.Contains("Uwaga / nr") && change.Before == "old" && change.After == "new");
    }
    internal static async Task<CrmClient> Api(NativeShiftReportsServer server) { var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(server.Role, "0000", default); return api; }
    internal static void Complete(ShiftReportsEditorViewModel model)
    {
        foreach (var row in model.HeaderFields.Where(row => row.Number).Concat(model.Quantities)) row.Value = "1";
        model.Field("damaged").Value = "0";
        model.Field("leader").Value = "Synthetic leader"; model.Field("responsible").Value = "Synthetic responsible";
        model.Field("productionPerson").Value = "Synthetic production"; model.Field("controller").Value = "Synthetic controller";
        foreach (var row in model.Equipment) row.Condition = "sprawne"; foreach (var row in model.Checks) row.Answer = "OK";
    }
}

internal sealed class NativeShiftReportsServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Role { get; set; } = "produkcja";
    public long ActorId { get; set; } = 101;
    public string? DefaultShift { get; set; }
    public bool LoseNextWrite { get; set; }
    public bool ExpireNextWrite { get; set; }
    public bool RejectNextWrite { get; set; }
    public int RejectStatus { get; set; }
    internal static ShiftReportFeatures DisabledFeatures { get; } = new(false, false, false, new Dictionary<int, IReadOnlyList<ShiftReportQuestion>> { [1] = Enumerable.Range(0, 9).Select(i => new ShiftReportQuestion($"synthetic-{i}", $"Synthetic fixed safety question {i + 1}")).ToArray() });
    public ShiftReportFeatures Features => DisabledFeatures with { Read = true, Write = Role is "produkcja" or "technolog", CanAdminDelete = Role is "technolog" or "ceo" };
    public static ShiftReportFields CompleteFields => new() { Leader = "Synthetic leader", Responsible = "Synthetic responsible", People = 3, Reference = "Synthetic free batch", Assembled = 10, Prepared = 10, Poured = 10, Checked = 10, Demoulded = 10, Damaged = 0, Vibrators = new() { Condition = "sprawne" }, Extensions = new() { Condition = "sprawne" }, Checks = Enumerable.Repeat<string?>("OK", 9).ToArray(), ProductionPerson = "Synthetic production", Controller = "Synthetic controller" };
    public ShiftReportDto Report { get; set; } = new(1, new(2026, 10, 1), "I", 101, "Synthetic report author", "draft", 1, 1, new() { Leader = "Synthetic leader", Responsible = "Synthetic responsible", ProductionPerson = "Synthetic production" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, 0, null, false, new() { ["controller"] = "Incomplete" });
    public Dictionary<Guid, object> Receipts { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> Paths { get; } = [];
    public TaskCompletionSource<HttpResponseMessage>? NextList { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath; Paths.Add(request.RequestUri.PathAndQuery);
        if (path.EndsWith("/session/login/password")) return EditorTests.Reply(new { id = ActorId, access_token = "synthetic-shift", refresh_token = "synthetic-device", default_shift = DefaultShift });
        if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-shift", default_shift = DefaultShift });
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(ActorId, "Synthetic report user", null, null, Role));
        if (path.EndsWith("/orders/features")) return EditorTests.Reply(new OrderFeatures(false));
        if (path.EndsWith("/shift-reports/features")) return EditorTests.Reply(Features);
        if (path.EndsWith("/today")) return EditorTests.Reply(new ShiftReportToday(Report.ReportDate, Features.CanAdminDelete, [Report], new(1, 17, 17, 17, 17, 17, 2)));
        if (path.Contains("/audit")) return EditorTests.Reply(new ShiftReportAuditDto[] { new(1, 101, "Synthetic report user", "corrected", "Synthetic recount", JsonSerializer.Deserialize<JsonElement>("{\"fields\":{\"poured\":10,\"extensions\":{\"note\":\"old\"}}}"), JsonSerializer.Deserialize<JsonElement>("{\"fields\":{\"poured\":12,\"extensions\":{\"note\":\"new\"}}}"), DateTimeOffset.UtcNow) });
        if (request.Method == HttpMethod.Get)
        {
            if (path.EndsWith("/shift-reports") && request.RequestUri.Query.Split('&').Any(part => part.TrimStart('?') is "dateFrom=" or "dateTo=" or "shift=")) return Error(400);
            if (path.EndsWith("/shift-reports/1")) return EditorTests.Reply(Report);
            if (NextList is { } delayed) { NextList = null; return await delayed.Task; }
            return EditorTests.Reply(new Page<ShiftReportDto>([Report], 51, request.RequestUri.Query.Contains("page=2") ? 2 : 1, 50));
        }
        var body = await request.Content!.ReadAsStringAsync(ct); Bodies.Add(body); var input = JsonSerializer.Deserialize<JsonElement>(body); var id = input.GetProperty("requestId").GetGuid();
        if (ExpireNextWrite) { ExpireNextWrite = false; return Error(401); }
        if (RejectStatus != 0) { var status = RejectStatus; RejectStatus = 0; return Error(status); }
        if (RejectNextWrite) { RejectNextWrite = false; return EditorTests.Reply(new ApiError(422, "Synthetic incomplete report", new() { ["controller"] = ["Synthetic missing controller"] }), HttpStatusCode.UnprocessableEntity); }
        if (!Receipts.TryGetValue(id, out var result))
        {
            if (input.TryGetProperty("expectedVersion", out var expected) && expected.GetInt64() != Report.Version) return Error(409);
            if (path.EndsWith("/discard") || path.EndsWith("/admin-delete")) result = new ShiftReportMutationResult(1, Report.Version + 1, true);
            else if (path.EndsWith("/save") || path.EndsWith("/corrections"))
            {
                var fields = input.GetProperty("fields").Deserialize<ShiftReportFields>(Json)!;
                var correct = path.EndsWith("/corrections"); result = Report = Report with { Fields = fields, Version = Report.Version + 1, Status = correct ? "corrected" : "draft", CorrectionCount = Report.CorrectionCount + (correct ? 1 : 0), ValidationErrors = fields.Controller.Length > 0 ? [] : new() { ["controller"] = "Incomplete" } };
            }
            else if (path.EndsWith("/finalize")) result = Report = Report with { Status = "finalized", Version = Report.Version + 1, FinalizedAt = DateTimeOffset.UtcNow, FinalizedByName = "Synthetic report user" };
            else { var create = input.Deserialize<CreateShiftReport>(Json)!; result = Report = Report with { ReportDate = create.ReportDate, Shift = create.Shift }; }
            Receipts.Add(id, result);
        }
        if (LoseNextWrite) { LoseNextWrite = false; throw new HttpRequestException("Synthetic lost shift response"); }
        return EditorTests.Reply(result);
    }
    private static HttpResponseMessage Error(int status) => EditorTests.Reply(new ApiError(status, "Synthetic shift error"), (HttpStatusCode)status);
}
