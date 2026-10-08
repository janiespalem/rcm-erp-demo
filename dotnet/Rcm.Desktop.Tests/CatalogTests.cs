using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class CatalogTests
{
    [Theory]
    [InlineData(CatalogKind.Materials)]
    [InlineData(CatalogKind.Operations)]
    public async Task Lost_create_response_replays_identical_command_and_retains_fields(CatalogKind kind)
    {
        var server = new NativeCatalogServer { LoseNextWrite = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(kind) { WriteEnabled = true, Name = "Synthetic reusable entry", Group = "Synthetic group", Rate = "4,50", Notes = "Synthetic material notes", Formula = "synthetic keywords" };
        Assert.False(await model.Save(api, default));
        Assert.True(model.Uncertain); Assert.True(model.Dirty); Assert.False(model.CanEdit);
        Assert.Equal("Synthetic reusable entry", model.Name); Assert.Equal("4,50", model.Rate);
        Assert.True(await model.Save(api, default));
        Assert.Equal(server.WriteBodies[0], server.WriteBodies[1]); Assert.Single(server.Receipts);
        Assert.True(model.Completed); Assert.False(model.Dirty);
        Assert.Equal("Synthetic reusable entry", model.SavedRow!.Name);
        Assert.Equal(kind == CatalogKind.Materials ? "Synthetic material notes" : null, model.SavedRow.Notes);
        Assert.Equal(kind == CatalogKind.Operations ? "synthetic keywords" : null, model.SavedRow.Formula);
    }

    [Fact]
    public async Task Conflict_comparison_retains_draft_and_uses_explicitly_accepted_version()
    {
        var server = new NativeCatalogServer();
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(CatalogKind.Operations, CatalogRow.From(server.Operation)) { WriteEnabled = true, Name = "Synthetic local operation", Formula = "synthetic local keywords" };
        server.Operation = server.Operation with { Name = "Synthetic remote operation", Version = 2 };
        Assert.False(await model.Save(api, default));
        Assert.True(model.HasConflict); Assert.False(model.CanSave);
        await model.Compare(api, default);
        Assert.Contains("Synthetic remote operation", model.Comparison);
        Assert.Contains("Synthetic local operation", model.Comparison);
        Assert.True(model.CanAccept); model.AcceptComparison();
        Assert.Equal("Synthetic local operation", model.Name); Assert.Equal("synthetic local keywords", model.Formula);
        Assert.True(await model.Save(api, default));
        Assert.Equal(3, model.SavedRow!.Version); Assert.Equal("Synthetic local operation", server.Operation.Name);
        Assert.NotEqual(server.WriteBodies[0], server.WriteBodies[1]);
    }

    [Theory]
    [InlineData(CatalogKind.Materials)]
    [InlineData(CatalogKind.Operations)]
    public async Task Lost_archive_or_delete_response_retries_one_command(CatalogKind kind)
    {
        var server = new NativeCatalogServer { LoseNextWrite = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var row = kind == CatalogKind.Materials ? CatalogRow.From(server.Material) : CatalogRow.From(server.Operation);
        var model = new CatalogEditorViewModel(kind, row, remove: true) { WriteEnabled = true };
        Assert.False(await model.Save(api, default)); Assert.True(model.Uncertain);
        Assert.True(await model.Save(api, default));
        Assert.Single(server.Receipts); Assert.Equal(server.WriteBodies[0], server.WriteBodies[1]);
        Assert.Equal(kind == CatalogKind.Operations, server.OperationDeleted);
        Assert.Equal(kind == CatalogKind.Operations, server.Material.IsActive);
    }

    [Fact]
    public async Task Archived_material_can_be_restored_without_losing_notes()
    {
        var server = new NativeCatalogServer(); server.Material = server.Material with { IsActive = false, Notes = "Synthetic retained notes" };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(CatalogKind.Materials, CatalogRow.From(server.Material)) { WriteEnabled = true, Active = true };
        Assert.True(await model.Save(api, default)); Assert.True(server.Material.IsActive);
        Assert.Equal("Synthetic retained notes", server.Material.Notes);
    }

    [Fact]
    public async Task Reauthentication_keeps_request_identity_and_draft()
    {
        var server = new NativeCatalogServer { ExpireNextWrite = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(CatalogKind.Materials) { WriteEnabled = true, Name = "Synthetic retained" };
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, default))).Status);
        Assert.True(model.Uncertain); Assert.Equal("Synthetic retained", model.Name);
        await api.Login("technolog", "0000", default);
        Assert.True(await model.Save(api, default)); Assert.Equal(server.WriteBodies[0], server.WriteBodies[1]);
    }

    [Fact]
    public async Task Validation_rejects_unstorable_material_price_and_retains_input()
    {
        var server = new NativeCatalogServer(); using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(CatalogKind.Materials) { WriteEnabled = true, Name = "Synthetic", Rate = "10000" };
        Assert.False(await model.Save(api, default)); Assert.NotEmpty(model.RateError);
        Assert.Equal("10000", model.Rate); Assert.True(model.Dirty); Assert.Empty(server.WriteBodies);
    }

    [Fact]
    public async Task Server_field_errors_preserve_form_and_leave_it_editable()
    {
        var server = new NativeCatalogServer { RejectNextWrite = true }; using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        var model = new CatalogEditorViewModel(CatalogKind.Operations) { WriteEnabled = true, Name = "Synthetic retained", Group = "Synthetic department" };
        Assert.False(await model.Save(api, default)); Assert.True(model.CanEdit); Assert.True(model.Dirty);
        Assert.Equal("Synthetic field error", model.NameError); Assert.Equal("Synthetic department", model.Group);
        Assert.True(await model.Save(api, default)); Assert.NotEqual(server.WriteBodies[0], server.WriteBodies[1]);
    }

    [Theory]
    [InlineData("biuro", true)]
    [InlineData("ceo", true)]
    [InlineData("technolog", false)]
    public async Task Role_or_writer_gate_prevents_catalog_mutation(string role, bool write)
    {
        var server = new NativeCatalogServer { Role = role }; using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login(role, "0000", default);
        using var shell = new MainViewModel(api);
        Assert.Equal(role == "technolog", shell.CanManageCatalog);
        var model = new CatalogEditorViewModel(CatalogKind.Materials) { WriteEnabled = write, Name = "Synthetic" };
        Assert.False(await model.Save(api, default)); Assert.Empty(server.WriteBodies); Assert.True(model.Dirty);
    }

    [Fact]
    public async Task Old_search_result_cannot_overwrite_new_catalog_page()
    {
        var server = new NativeCatalogServer(); using var api = new CrmClient(new Uri("http://localhost/"), server);
        var model = new CatalogViewModel(api, CatalogKind.Materials) { Available = true, Search = "old" };
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.NextList = delayed;
        var old = model.Load(default);
        model.Search = "name / steel"; model.Page = 2; model.IncludeArchived = true;
        await model.Load(default);
        delayed.SetResult(EditorTests.Reply(new Page<CatalogMaterialDto>([server.Material with { Name = "Synthetic stale" }], 1, 1, 50)));
        await old;
        Assert.Equal(server.Material.Name, Assert.Single(model.Rows).Name); Assert.Equal(2, model.Page);
        Assert.Contains("q=name%20%2F%20steel", server.Paths[^1]);
        Assert.Contains("includeArchived=true", server.Paths[^1]); Assert.False(model.Busy);
    }

    [Fact]
    public async Task Session_renewal_preserves_catalog_section_and_logout_clears_access()
    {
        var server = new NativeCatalogServer(); using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", default);
        using var shell = new MainViewModel(api) { Section = MainSection.Materials };
        await api.Login("technolog", "0000", default);
        Assert.True(shell.IsMaterialsVisible); Assert.Equal("Materiały", shell.PageTitle);
        api.Logout(); Assert.False(shell.CanManageCatalog); Assert.False(shell.IsMaterialsVisible);
    }
}

internal sealed class NativeCatalogServer : HttpMessageHandler
{
    public string Role { get; set; } = "technolog";
    public bool WriteEnabled { get; set; } = true;
    public bool LoseNextWrite { get; set; }
    public bool ExpireNextWrite { get; set; }
    public bool RejectNextWrite { get; set; }
    public bool OperationDeleted { get; private set; }
    public CatalogMaterialDto Material { get; set; } = new(1, "Synthetic S355 steel", "stal", 4.5, true, "Synthetic material note", 1);
    public CatalogOperationDto Operation { get; set; } = new(2, "Synthetic welding", "Spawalnia", 90, "synthetic welding frame", 1);
    public Dictionary<Guid, object> Receipts { get; } = [];
    public List<string> WriteBodies { get; } = [];
    public List<string> Paths { get; } = [];
    public TaskCompletionSource<HttpResponseMessage>? NextList { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/shift-reports/features") return EditorTests.Reply(NativeShiftReportsServer.DisabledFeatures); Paths.Add(request.RequestUri.PathAndQuery);
        if (path.EndsWith("/session/login")) return EditorTests.Reply(new { access_token = "synthetic-catalog" });
        if (path.EndsWith("/session")) return EditorTests.Reply(new SessionDto(101, "Synthetic catalog user", null, null, Role));
        if (path.EndsWith("/features")) return EditorTests.Reply(new CatalogFeatures(true, true, WriteEnabled, 95));
        var material = path.Contains("/materials");
        if (request.Method == HttpMethod.Get)
        {
            if (path.EndsWith("/materials/1")) return EditorTests.Reply(Material);
            if (path.EndsWith("/operations/2")) return EditorTests.Reply(Operation);
            if (NextList is { } delayed) { NextList = null; return await delayed.Task; }
            var page = request.RequestUri.Query.Contains("page=2") ? 2 : 1;
            var showMaterial = Material.IsActive || request.RequestUri.Query.Contains("includeArchived=true");
            return material ? EditorTests.Reply(new Page<CatalogMaterialDto>(showMaterial ? [Material] : [], showMaterial ? 51 : 0, page, 50)) : EditorTests.Reply(new Page<CatalogOperationDto>(OperationDeleted ? [] : [Operation], OperationDeleted ? 0 : 1, page, 50));
        }
        var body = await request.Content!.ReadAsStringAsync(ct); WriteBodies.Add(body);
        var input = JsonSerializer.Deserialize<JsonElement>(body); var id = input.GetProperty("requestId").GetGuid();
        if (ExpireNextWrite) { ExpireNextWrite = false; return Error(401); }
        if (RejectNextWrite)
        {
            RejectNextWrite = false;
            return EditorTests.Reply(new ApiError(422, "Synthetic validation", new() { ["name"] = ["Synthetic field error"] }), HttpStatusCode.UnprocessableEntity);
        }
        if (!Receipts.TryGetValue(id, out var result))
        {
            if (input.TryGetProperty("expectedVersion", out var expected) && expected.GetInt64() != (material ? Material.Version : Operation.Version)) return Error(409);
            if (path.EndsWith("/archive") || path.EndsWith("/delete"))
            {
                if (material) Material = Material with { IsActive = false, Version = Material.Version + 1 };
                else { Operation = Operation with { Version = Operation.Version + 1 }; OperationDeleted = true; }
                result = new CatalogMutationResult(material ? Material.Id : Operation.Id, material ? Material.Version : Operation.Version, !material);
            }
            else if (material)
            {
                var command = JsonSerializer.Deserialize<CreateCatalogMaterial>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                result = Material = new(Material.Id, command.Name, command.Category, command.DefaultRatePlnKg, command.IsActive, command.Notes, path.EndsWith("/update") ? Material.Version + 1 : 1);
            }
            else
            {
                var command = JsonSerializer.Deserialize<CreateCatalogOperation>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                result = Operation = new(Operation.Id, command.Name, command.Department, command.DefaultRate, command.Formula, path.EndsWith("/update") ? Operation.Version + 1 : 1);
            }
            Receipts.Add(id, result);
        }
        if (LoseNextWrite) { LoseNextWrite = false; throw new HttpRequestException("Synthetic lost catalog reply"); }
        return EditorTests.Reply(result);
    }
    private static HttpResponseMessage Error(int status) => EditorTests.Reply(new ApiError(status, "Synthetic catalog error"), (HttpStatusCode)status);
}
