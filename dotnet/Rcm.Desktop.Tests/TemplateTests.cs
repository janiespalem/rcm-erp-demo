using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class TemplateTests
{
    internal static TemplateFeatures Features => new(true, true, true, true, true, 95);
    [Fact]
    public async Task Lost_save_retries_frozen_payload_and_preserves_legacy_metadata_and_project_price()
    {
        var server = new NativeTemplateServer { LoseNextWrite = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("technolog", "0000", default);
        var model = new TemplateEditorViewModel(server.Template, Features, true) { Name = "Synthetic local project", BasePrice = "123" };
        model.Operations[0].Hours = "2,5"; model.Materials[0].Quantity = "3";
        Assert.False(await model.Save(api, default)); Assert.True(model.Uncertain); Assert.True(model.Dirty); Assert.False(model.CanEdit);
        model.Name = "Synthetic mutation while frozen";
        Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]); Assert.Single(server.Receipts);
        Assert.Equal("Synthetic local project", server.Template.Name); Assert.Equal(750, server.Template.BasePricePln);
        Assert.Equal("retained", server.Template.Operations[0].GetProperty("metadata").GetProperty("fixture").GetString());
        Assert.Equal(2.5, server.Template.Operations[0].GetProperty("hours").GetDouble());
        Assert.Equal(JsonValueKind.Null, server.Template.Materials[1].ValueKind); Assert.Equal(7, server.Template.Materials[2].GetInt32());
        Assert.Equal("legacy string", server.Template.Materials[3].GetString()); Assert.False(model.Dirty);
    }
    [Fact]
    public async Task Upload_retry_keeps_request_id_and_bytes_after_source_is_replaced_and_requires_review_before_apply()
    {
        var server = new NativeTemplateServer(); using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("technolog", "0000", default);
        var model = new TemplateEditorViewModel(server.Template, Features); model.RefreshAccess(api);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            await File.WriteAllTextAsync(file, "%PDF-1.4 synthetic immutable drawing"); await model.SelectDrawing(file, default);
            await File.WriteAllTextAsync(file, "%PDF-1.4 different source"); server.LoseNextWrite = true;
            Assert.False(await model.Upload(api, default)); Assert.True(model.Uncertain); Assert.False(model.CanDiscardDrawing);
            Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[0], server.Bodies[1]);
            Assert.Equal(server.UploadIds[0], server.UploadIds[1]); Assert.Contains("immutable drawing", server.Bodies[0]);
            Assert.False(model.CanApply); await model.PreviewDrawing(api, default); Assert.True(model.CanApply);
            Assert.Equal("Synthetic reviewed steel", Assert.Single(model.PreviewMaterials).Name); Assert.Equal("Synthetic project part", server.Template.Name);
            Assert.True(await model.Apply(api, default)); Assert.Equal("Synthetic extracted part", server.Template.Name); Assert.False(model.Dirty);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task Conflict_requires_comparison_and_acceptance_without_losing_local_rows()
    {
        var server = new NativeTemplateServer(); using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("technolog", "0000", default);
        var model = new TemplateEditorViewModel(server.Template, Features, true) { Name = "Synthetic local draft" };
        model.Materials[0].Dimension = "10 x 20"; server.Template = server.Template with { Name = "Synthetic remote", BasePricePln = 950, Version = 2 };
        Assert.False(await model.Save(api, default)); Assert.True(model.HasConflict); Assert.False(model.CanSave);
        await model.Compare(api, default); Assert.Contains("Synthetic remote", model.Comparison); model.AcceptComparison();
        Assert.Equal("10 x 20", model.Materials[0].Dimension); Assert.Equal("Synthetic local draft", model.Name);
        Assert.True(await model.Save(api, default)); Assert.Equal(950, server.Template.BasePricePln); Assert.Equal(3, server.Template.Version);
    }
    [Fact]
    public async Task Server_validation_and_expired_login_keep_draft_and_uncertain_request_identity()
    {
        var server = new NativeTemplateServer { RejectNextWrite = true }; using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("technolog", "0000", default);
        var model = new TemplateEditorViewModel(null, Features) { Name = "Synthetic retained form", Notes = "Synthetic retained note" };
        Assert.False(await model.Save(api, default)); Assert.True(model.CanEdit); Assert.Contains("Synthetic field error", model.NameError);
        server.ExpireNextWrite = true; Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, default))).Status);
        Assert.True(model.Uncertain); Assert.Equal("Synthetic retained note", model.Notes); await api.Login("technolog", "0000", default);
        Assert.True(await model.Retry(api, default)); Assert.Equal(server.Bodies[1], server.Bodies[2]); Assert.NotEqual(server.Bodies[0], server.Bodies[1]);
    }
    [Theory]
    [InlineData("biuro", true)] [InlineData("ceo", true)] [InlineData("technolog", false)]
    public async Task Readonly_role_or_writer_gate_prevents_writes_and_project_price_is_quote_label(string role, bool write)
    {
        var server = new NativeTemplateServer { Role = role }; using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login(role, "0000", default);
        var model = new TemplateEditorViewModel(server.Template, Features with { Write = write }) { Notes = "Synthetic change" };
        Assert.False(await model.Save(api, default)); Assert.Empty(server.Bodies);
        Assert.Equal("Do wyceny", new TemplateListRow(server.Template, true).Price);
        using var shell = new MainViewModel(api) { Section = role == "technolog" ? MainSection.TemplateProjects : MainSection.TemplateCatalog };
        await api.Login(role, "0000", default); Assert.True(shell.IsTemplatesVisible); api.Logout(); Assert.False(shell.IsTemplatesVisible);
    }
    [Fact]
    public async Task Newest_search_wins_even_when_old_response_ignores_cancellation()
    {
        var server = new NativeTemplateServer(); using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("technolog", "0000", default);
        var model = new TemplateWorkspaceViewModel(api) { Search = "old" }; model.SetFeatures(Features);
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); server.NextList = delayed;
        var old = model.Load(default); model.Search = "new / query"; model.Page = 2; await model.Load(default);
        delayed.SetResult(EditorTests.Reply(new Page<ProductTemplateDto>([server.Template with { Name = "Synthetic stale" }], 1, 1, 50))); await old;
        Assert.Equal(server.Template.Name, Assert.Single(model.Rows).Name); Assert.Equal(2, model.Page); Assert.Contains("q=new%20%2F%20query", server.Paths[^1]);
    }
}

internal sealed class NativeTemplateServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static JsonElement Array(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    public string Role { get; set; } = "technolog";
    public bool LoseNextWrite { get; set; }
    public bool RejectNextWrite { get; set; }
    public bool ExpireNextWrite { get; set; }
    public ProductTemplateDto Template { get; set; } = new(1, "Synthetic project part", "remont", Array("[{\"op\":\"Synthetic welding\",\"hours\":1,\"rate_per_hour\":90,\"metadata\":{\"fixture\":\"retained\"}}]"),
        Array("[{\"mat\":\"Synthetic steel\",\"dim\":\"20x30\",\"qty\":1,\"unit\":\"szt\",\"legacy\":42},null,7,\"legacy string\"]"), Array("[\"Synthetic instruction\"]"), Array("[\"Synthetic CNC\"]"), 750, .25, true, "SYNTHETIC-PROJECT", "001", "Synthetic notes", false, 1);
    public Dictionary<Guid, ProductTemplateDto> Receipts { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> UploadIds { get; } = [];
    public List<string> Paths { get; } = [];
    public TaskCompletionSource<HttpResponseMessage>? NextList { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/shift-reports/features") return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures); Paths.Add(request.RequestUri.PathAndQuery);
        if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-templates" });
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(101, "Synthetic template user", null, null, Role));
        if (path.EndsWith("/orders/features")) return EditorTests.Reply(new OrderFeatures(false));
        if (path.EndsWith("/templates/features")) return EditorTests.Reply(TemplateTests.Features);
        if (path.EndsWith("/drawing/preview")) return EditorTests.Reply(new TemplateDrawingPreview("SYN-01", "Synthetic extracted part", "S355", 4, "L20", 1,
            Array("[{\"mat\":\"Synthetic reviewed steel\",\"dim\":\"20x20\",\"qty\":2,\"unit\":\"szt\"}]"), Array("[{\"op\":\"Synthetic reviewed cut\",\"hours\":1}]"), "Synthetic PDF extraction for human review"));
        if (request.Method == HttpMethod.Get)
        {
            if (path.EndsWith("/projects")) return EditorTests.Reply(new Page<TemplateProjectDto>([new(Template.ProjectCode!, 1)], 1, 1, 50));
            if (path.EndsWith("/templates/1")) return EditorTests.Reply(Template);
            if (NextList is { } delayed) { NextList = null; return await delayed.Task; }
            return EditorTests.Reply(new Page<ProductTemplateDto>([Template], 51, request.RequestUri.Query.Contains("page=2") ? 2 : 1, 50));
        }
        var body = await request.Content!.ReadAsStringAsync(ct); Bodies.Add(body);
        var upload = request.Method == HttpMethod.Put;
        var input = upload ? default : JsonSerializer.Deserialize<JsonElement>(body);
        var id = upload ? Guid.Parse(request.Headers.GetValues("X-Request-Id").Single()) : input.GetProperty("requestId").GetGuid();
        if (upload) { Assert.Equal("application/pdf", request.Content.Headers.ContentType!.MediaType); UploadIds.Add(id.ToString()); }
        if (ExpireNextWrite) { ExpireNextWrite = false; return Error(401); }
        if (RejectNextWrite) { RejectNextWrite = false; return EditorTests.Reply(new ApiError(422, "Synthetic validation", new() { ["name"] = ["Synthetic field error"] }), HttpStatusCode.UnprocessableEntity); }
        if (!Receipts.TryGetValue(id, out var result))
        {
            var expected = upload ? long.Parse(request.Headers.GetValues("X-Expected-Version").Single()) : input.TryGetProperty("expectedVersion", out var version) ? version.GetInt64() : 0;
            if (expected != 0 && expected != Template.Version) return Error(409);
            if (upload) result = Template = Template with { HasDrawing = true, Version = Template.Version + 1 };
            else if (path.EndsWith("/drawing/apply")) result = Template = Template with { Name = "Synthetic extracted part", Version = Template.Version + 1 };
            else if (path.EndsWith("/archive") || path.EndsWith("/restore")) result = Template = Template with { IsActive = path.EndsWith("/restore"), Version = Template.Version + 1 };
            else
            {
                var draft = input.GetProperty("draft").Deserialize<ProductTemplateDraft>(Json)!;
                result = Template = new(1, draft.Name, draft.Category, draft.Operations, draft.Materials, draft.Instructions, draft.Machines,
                    draft.BasePricePln, draft.MarginPct, true, draft.ProjectCode, draft.PositionNumber, draft.Notes, Template.HasDrawing, expected == 0 ? 1 : Template.Version + 1);
            }
            Receipts.Add(id, result);
        }
        if (LoseNextWrite) { LoseNextWrite = false; throw new HttpRequestException("Synthetic lost response"); }
        return EditorTests.Reply(result);
    }
    private static HttpResponseMessage Error(int status) => EditorTests.Reply(new ApiError(status, "Synthetic template error"), (HttpStatusCode)status);
}
