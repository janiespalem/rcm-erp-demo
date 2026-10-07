using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderWorkflowTests
{
    [Fact]
    public async Task Resource_reauthentication_keeps_draft_and_request_identity()
    {
        var server = new NativeOrdersServer { Role = "technolog", RejectResourceOnce = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("technolog", "0000", CancellationToken.None);
        var model = new OrderResourcesViewModel(server.Order);
        await model.Load(api, CancellationToken.None);
        Assert.True(model.BeginQuestion()); model.Draft = "Synthetic thickness?";
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => model.Save(api, CancellationToken.None))).Status);
        Assert.True(model.Uncertain); Assert.False(model.CanEdit); Assert.Equal("Synthetic thickness?", model.Draft);
        await api.Login("technolog", "0000", CancellationToken.None);
        Assert.True(await model.Save(api, CancellationToken.None));
        Assert.Single(server.Questions); Assert.Equal(server.ResourceRequestIds[0], server.ResourceRequestIds[1]);
    }

    [Fact]
    public async Task Lost_create_response_reuses_the_same_command_without_losing_values()
    {
        var server = new NativeOrdersServer { LoseCreateResponse = true };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        var editor = new OrderEditorViewModel { Client = "Synthetic customer", Description = "Repair synthetic frame", Quantity = "3" };
        Assert.False(await editor.Save(api, CancellationToken.None));
        Assert.True(editor.Uncertain); Assert.False(editor.CanEdit); Assert.True(editor.Dirty);
        Assert.Equal("Repair synthetic frame", editor.Description);
        Assert.True(await editor.Save(api, CancellationToken.None));
        Assert.Single(server.Receipts); Assert.Equal(server.RequestIds[0], server.RequestIds[1]); Assert.Equal(3, editor.Saved!.Quantity);
    }
    [Fact]
    public async Task Invalid_quantity_stays_in_form_and_sends_nothing()
    {
        var server = new NativeOrdersServer(); using var api = new CrmClient(new Uri("http://localhost/"), server);
        var editor = new OrderEditorViewModel { Client = "Synthetic", Description = "Repair", Quantity = "1,5" };
        Assert.False(await editor.Save(api, CancellationToken.None));
        Assert.Equal("1,5", editor.Quantity); Assert.NotEmpty(editor.QuantityError); Assert.Empty(server.RequestIds);
    }
    [Fact]
    public async Task Expired_session_keeps_create_identity_until_the_user_retries()
    {
        var server = new NativeOrdersServer { RejectCreateOnce = true }; using var api = new CrmClient(new Uri("http://localhost/"), server);
        var editor = new OrderEditorViewModel { Client = "Synthetic", Description = "Repair" };
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiFailure>(() => editor.Save(api, CancellationToken.None))).Status);
        Assert.Equal("Repair", editor.Description); Assert.False(editor.CanEdit);
        Assert.True(await editor.Save(api, CancellationToken.None));
        Assert.Equal(server.RequestIds[0], server.RequestIds[1]);
    }
    [Fact]
    public async Task Quote_uncertain_save_requires_verification_and_preserves_the_input()
    {
        var server = new NativeOrdersServer { LoseQuoteResponse = true }; using var api = new CrmClient(new Uri("http://localhost/"), server);
        var model = new OrderQuoteViewModel(server.Order);
        await model.Load(api, CancellationToken.None);
        model.Method = "reczna"; model.ManualTotal = "2450,25";
        Assert.False(await model.Save(api, CancellationToken.None)); Assert.True(model.Uncertain); Assert.False(model.CanSave);
        Assert.False(await model.Save(api, CancellationToken.None)); Assert.Equal(1, server.QuoteSaves);
        await model.Verify(api, CancellationToken.None); model.AcceptVerified();
        Assert.True(model.CanSave); Assert.Equal("2450,25", model.ManualTotal);
    }
    [Fact]
    public async Task Quote_failure_before_first_commit_can_be_retried_without_retyping()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(404, "Missing"), HttpStatusCode.NotFound)));
        handler.Replies.Enqueue(_ => throw new HttpRequestException("Request did not reach server"));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new ApiError(404, "Missing"), HttpStatusCode.NotFound)));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderQuoteViewModel(new() { Id = 1 }); await model.Load(api, CancellationToken.None);
        model.Method = "reczna"; model.ManualTotal = "123,45";
        Assert.False(await model.Save(api, CancellationToken.None));
        await model.Verify(api, CancellationToken.None); model.AcceptVerified();
        Assert.True(model.CanSave); Assert.Equal("123,45", model.ManualTotal);
    }
    [Fact]
    public async Task Read_only_role_cannot_mutate_orders_and_archive_is_read_only()
    {
        var server = new NativeOrdersServer { Role = "ceo" }; using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login("ceo", "0000", CancellationToken.None);
        var model = new OrdersViewModel(api); await model.Open(1, CancellationToken.None);
        Assert.False(model.CanCreate); Assert.False(model.CanEdit); Assert.False(model.CanQuote);
        server.Role = "technolog"; await api.Login("technolog", "0000", CancellationToken.None);
        server.Order = server.Order with { ArchivedAt = DateTimeOffset.UtcNow };
        await model.Open(1, CancellationToken.None);
        Assert.False(model.CanEdit); Assert.False(model.CanTriage); Assert.True(model.CanRestore);
    }
    [Fact]
    public async Task Fresh_internal_quote_has_no_implicit_markup_and_mass_uses_selected_basis()
    {
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new ApiError(404, "Missing")) }));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new OrderQuotePreview(0, 50, 0, 50, 50, 50, 50, "od_masy", "brutto"))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new OrderQuoteViewModel(new() { Id = 1, IsInternal = true });
        await form.Load(api, CancellationToken.None);
        Assert.Equal("0", form.Overhead); Assert.Equal("0", form.Margin); Assert.False(form.ShowUnitPrices);
        form.Method = "od_masy"; form.WeightBasis = "brutto"; form.WeightRate = "5";
        form.Materials.Add(new() { Name = "Synthetic material", Quantity = "10", Price = "5" });
        await form.Preview(api, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(10, json.RootElement.GetProperty("weightKg").GetDouble());
        Assert.Equal(10, json.RootElement.GetProperty("materialWeightKg").GetDouble());
    }
    [Fact]
    public async Task Legacy_fixed_operation_cost_is_not_replaced_by_explicit_zero_hours()
    {
        var quote = new OrderQuoteDto(1, 1, 400, "kalkulacja", "netto", 0, 0, 0, 0, DateTimeOffset.UtcNow, null)
        { ProcessesJson = [new("Synthetic fixed cost", Cost: 400)], OverheadPct = 0 };
        var handler = new EditorTests.Responses(); handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(quote)));
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new OrderQuotePreview(400, 0, 0, 0, 400, 400, 400, "kalkulacja", "netto"))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var form = new OrderQuoteViewModel(new() { Id = 1 }); await form.Load(api, CancellationToken.None);
        await form.Preview(api, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Bodies[1]);
        var process = json.RootElement.GetProperty("processes")[0];
        Assert.Equal(JsonValueKind.Null, process.GetProperty("hours").ValueKind);
        Assert.Equal(JsonValueKind.Null, process.GetProperty("ratePerHour").ValueKind);
        Assert.Equal(400, process.GetProperty("cost").GetDouble());
    }
    [Fact]
    public async Task Stale_search_does_not_replace_latest_results_when_transport_ignores_cancellation()
    {
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new EditorTests.Responses();
        handler.Replies.Enqueue(_ => delayed.Task);
        handler.Replies.Enqueue(_ => Task.FromResult(EditorTests.Reply(new Page<OrderDto>([new() { Id = 2, Client = "new" }], 1, 1, 50))));
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrdersViewModel(api) { Search = "old" };
        var old = model.Load(CancellationToken.None); model.Search = "new"; await model.Load(CancellationToken.None);
        delayed.SetResult(EditorTests.Reply(new Page<OrderDto>([new() { Id = 1, Client = "old" }], 1, 1, 50)));
        await old; Assert.Equal("new", Assert.Single(model.Rows).Client);
    }
}

internal sealed class NativeOrdersServer : HttpMessageHandler
{
    public string Role { get; set; } = "biuro";
    public bool LoseCreateResponse { get; set; }
    public bool RejectCreateOnce { get; set; }
    public int IntakeRequests { get; private set; }
    public Dictionary<Guid, OrderIntakeResult> IntakeReceipts { get; } = [];
    public bool LoseQuoteResponse { get; set; }
    public bool LoseQuestionResponse { get; set; }
    public bool RejectResourceOnce { get; set; }
    public bool LoseTemplateResponse { get; set; }
    public Dictionary<Guid, OrderSavedTemplate> TemplateReceipts { get; } = [];
    public List<Guid> TemplateRequestIds { get; } = [];
    public Dictionary<Guid, object> ResourceReceipts { get; } = [];
    public List<Guid> ResourceRequestIds { get; } = [];
    public OrderOperationRow Operation { get; set; } = new(1, "Spawanie", "Spawalnia", null, 1, "pending", null);
    public List<OrderQuestionDto> Questions { get; } = [];
    public int QuoteSaves { get; private set; }
    public TaskCompletionSource<HttpResponseMessage>? NextList { get; set; }
    public TaskCompletionSource<HttpResponseMessage>? PendingTriage { get; set; }
    public List<Guid> RequestIds { get; } = [];
    public Dictionary<Guid, OrderDto> Receipts { get; } = [];
    public OrderDto Order { get; set; } = new() { Id = 1, VersionId = 1, OrderNumber = "1/2026", Client = "Synthetic order customer", Description = "Synthetic repair", Deadline = new DateOnly(2026, 10, 10), Quantity = 1, CreatedAt = DateTimeOffset.UtcNow };
    public OrderQuoteDto? Quote { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/session/login") return EditorTests.Reply(new { access_token = "synthetic-order-token" });
        if (path == "/api/v1/session") return EditorTests.Reply(new SessionDto(101, "Synthetic order user", null, null, Role));
        if (path == "/api/v1/orders/features") return EditorTests.Reply(new OrderFeatures(true, true, true, true, true));
        if (path == "/api/v1/orders/lookups") return EditorTests.Reply(new OrderLookups([], [], [], 90));
        if (path.Contains("/documents/"))
        {
            var content = new ByteArrayContent(path.EndsWith("/operations") ? "PK\u0003\u0004Synthetic"u8.ToArray() : "%PDF-1.7\nSynthetic"u8.ToArray());
            content.Headers.ContentType = new(path.EndsWith("/operations") ? "application/zip" : "application/pdf");
            return new(HttpStatusCode.OK) { Content = content };
        }
        if (path.EndsWith("/save-as-template"))
        {
            var input = (await request.Content!.ReadFromJsonAsync<SaveOrderAsTemplate>(ct))!;
            TemplateRequestIds.Add(input.RequestId);
            if (!TemplateReceipts.TryGetValue(input.RequestId, out var template))
                TemplateReceipts.Add(input.RequestId, template = new(42, input.Name!, input.Category!));
            if (LoseTemplateResponse) { LoseTemplateResponse = false; throw new HttpRequestException("Synthetic lost template reply"); }
            return EditorTests.Reply(template, HttpStatusCode.Created);
        }
        if (path.EndsWith("/attachments")) return EditorTests.Reply(new[] { new OrderAttachmentDto(1, 1, "synthetic-drawing.pdf", 20, "application/pdf", "technolog", DateTimeOffset.UtcNow) });
        if (path.EndsWith("/attachments/1/download")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.7\nSynthetic"u8.ToArray()) };
        if (path.EndsWith("/operations")) return EditorTests.Reply(new[] { Operation });
        if (path.EndsWith("/questions") && request.Method == HttpMethod.Get) return EditorTests.Reply(Questions.ToArray());
        if (path.EndsWith("/hours") || path.EndsWith("/answer") || path.EndsWith("/questions") && request.Method == HttpMethod.Post)
        {
            var input = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var id = input.GetProperty("requestId").GetGuid(); ResourceRequestIds.Add(id);
            if (RejectResourceOnce) { RejectResourceOnce = false; return Error(401); }
            if (!ResourceReceipts.TryGetValue(id, out var result))
            {
                if (path.EndsWith("/hours")) result = Operation = Operation with { ActualHours = input.GetProperty("actualHours").GetDouble() };
                else if (path.EndsWith("/answer"))
                {
                    var question = Questions.Single();
                    result = Questions[0] = question with { AnswerText = input.GetProperty("answerText").GetString(), Status = "answered", AnsweredAt = DateTimeOffset.UtcNow };
                }
                else
                {
                    var question = new OrderQuestionDto(Questions.Count + 1, 1, input.GetProperty("questionText").GetString()!, null, "pending", DateTimeOffset.UtcNow, null);
                    Questions.Add(question); result = question;
                }
                ResourceReceipts.Add(id, result);
            }
            if (LoseQuestionResponse && result is OrderQuestionDto) { LoseQuestionResponse = false; throw new HttpRequestException("Synthetic lost question response"); }
            return EditorTests.Reply(result);
        }
        if (path == "/api/v1/orders" && request.Method == HttpMethod.Get)
        {
            if (NextList is { } delayed) { NextList = null; return await delayed.Task; }
            return EditorTests.Reply(new Page<OrderDto>([Order], 1, 1, 50));
        }
        if (path == "/api/v1/orders" && request.Method == HttpMethod.Post)
        {
            var input = (await request.Content!.ReadFromJsonAsync<CreateOrder>(ct))!; RequestIds.Add(input.RequestId);
            if (RejectCreateOnce) { RejectCreateOnce = false; return Error(401); }
            if (!Receipts.TryGetValue(input.RequestId, out var saved))
            {
                saved = Order = Order with { Status = "draft", Client = input.Fields.Client, Description = input.Fields.Description, Deadline = input.Fields.Deadline, Quantity = input.Fields.Quantity };
                Receipts.Add(input.RequestId, saved);
            }
            if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("Synthetic lost reply after commit"); }
            return EditorTests.Reply(saved);
        }
        if (path == "/api/v1/orders/intake" && request.Method == HttpMethod.Post)
        {
            IntakeRequests++;
            var input = (await request.Content!.ReadFromJsonAsync<CreateOrder>(ct))!; RequestIds.Add(input.RequestId);
            if (RejectCreateOnce) { RejectCreateOnce = false; return Error(401); }
            if (!IntakeReceipts.TryGetValue(input.RequestId, out var saved))
            {
                var branch = input.Fields.IsInternal ? "standard" : "niestandard";
                Order = Order with { Status = input.Fields.IsInternal ? "in_production" : "niestandard", TriageBranch = branch, VersionId = 2,
                    Client = input.Fields.Client, Description = input.Fields.Description, Deadline = input.Fields.Deadline,
                    Quantity = input.Fields.Quantity, IsInternal = input.Fields.IsInternal };
                saved = new(Order, new(branch, "Do wyceny", null, null, ["Synthetic warning"]));
                IntakeReceipts.Add(input.RequestId, saved); Receipts.Add(input.RequestId, Order);
            }
            if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("Synthetic lost intake response after commit"); }
            return EditorTests.Reply(saved);
        }
        if (path.EndsWith("/events")) return EditorTests.Reply(new OrderEventDto[] { new(1, "created", null, "draft", "Synthetic user", "biuro", "Synthetic creation", DateTimeOffset.UtcNow) });
        if (path.EndsWith("/triage"))
        {
            Order = Order with { Status = "niestandard", VersionId = 2 };
            if (PendingTriage is { } delayed) { PendingTriage = null; return await delayed.Task; }
            return EditorTests.Reply(new OrderTriageResult("niestandard", "Do wyceny", null, null, []));
        }
        if (path.EndsWith("/quote/preview")) return EditorTests.Reply(new OrderQuotePreview(100, 50, 0, 0, 150, 165, 206.25, "kalkulacja", "netto"));
        if (path.EndsWith("/quote") && request.Method == HttpMethod.Get) return Quote is null ? Error(404) : EditorTests.Reply(Quote);
        if (path.EndsWith("/quote/manual") || path.EndsWith("/quote") && request.Method == HttpMethod.Post)
        {
            QuoteSaves++;
            var json = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var total = json.TryGetProperty("totalNet", out var amount) ? amount.GetDouble() : 206.25;
            Quote = new(1, 1, total, json.TryGetProperty("totalNet", out _) ? "reczna" : "kalkulacja", "netto", 0, 0, .25, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Order = Order with { Status = "quoted", VersionId = 3 };
            if (LoseQuoteResponse) { LoseQuoteResponse = false; throw new HttpRequestException("Synthetic lost quote reply"); }
            return EditorTests.Reply(Quote);
        }
        if (path == "/api/v1/orders/1") return EditorTests.Reply(Order);
        throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
    }
    private static HttpResponseMessage Error(int code) => new((HttpStatusCode)code) { Content = JsonContent.Create(new ApiError(code, "Synthetic error")) };
}
