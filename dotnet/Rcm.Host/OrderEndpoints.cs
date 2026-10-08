using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/api/v1/orders");
        orders.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var user = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            if (user.Role is not ("biuro" or "technolog" or "ceo")) throw new CrmFault(403, "Brak dostępu do zleceń.");
            http.Items["orderUser"] = user;
            return await next(context);
        });
        var native = app.Configuration["Orders:Mode"] == "native";
        orders.MapGet("/features", () => new OrderFeatures(native, native, true, native, native));
        if (native)
        {
            MapNative(orders);
            return;
        }
        orders.MapPost("/intake", (HttpContext h, CreateOrder command) =>
        {
            RequireEdit(h);
            throw new CrmFault(503, "Jednoczesny zapis i przekazanie zlecenia wymaga natywnej obsługi zleceń.");
        });
        orders.MapGet("", (HttpContext h, LegacyOrders o, CancellationToken ct, string? q = null, string? status = null,
            bool archived = false, int page = 1, int pageSize = 50) => o.Query(h, q, status, archived, page, pageSize, ct));
        orders.MapGet("/lookups", (HttpContext h, LegacyOrders o, CancellationToken ct) => o.Lookups(h, ct));
        orders.MapGet("/{id:long}", (HttpContext h, LegacyOrders o, long id, CancellationToken ct) => o.Detail(h, id, ct));
        orders.MapGet("/{id:long}/events", (HttpContext h, LegacyOrders o, long id, CancellationToken ct) => o.Events(h, id, ct));
        orders.MapGet("/{id:long}/quote", (HttpContext h, LegacyOrders o, long id, CancellationToken ct) => o.Quote(h, id, ct));
        orders.MapGet("/{id:long}/documents/{kind}", (HttpContext h, LegacyOrders o, long id, string kind, CancellationToken ct) =>
        { if (kind == "operations") RequireEdit(h, true); return o.DownloadDocument(h, id, kind, ct); });
        orders.MapPost("", (HttpContext h, LegacyOrders o, CreateOrder command, CancellationToken ct) =>
        { RequireEdit(h); return o.Create(h, command, ct); });
        orders.MapPut("/{id:long}", (HttpContext h, LegacyOrders o, long id, EditOrder command, CancellationToken ct) =>
        { RequireEdit(h); return o.Edit(h, id, command, ct); });
        orders.MapPost("/quote/preview", (HttpContext h, LegacyOrders o, OrderQuoteInput input, CancellationToken ct) => o.Preview(h, input, ct));
        orders.MapPost("/{id:long}/quote", (HttpContext h, LegacyOrders o, long id, OrderQuoteInput input, CancellationToken ct) =>
        { RequireEdit(h, true); return o.SaveQuote(h, id, input, ct); });
        orders.MapPost("/{id:long}/quote/manual", (HttpContext h, LegacyOrders o, long id, OrderManualQuote input, CancellationToken ct) =>
        { RequireEdit(h, true); return o.SaveManualQuote(h, id, input, ct); });
        orders.MapPost("/{id:long}/triage", (HttpContext h, LegacyOrders o, long id, CancellationToken ct) =>
        { RequireEdit(h); return o.Triage(h, id, ct); });
        foreach (var action in new[] { "confirm", "complete", "deliver", "archive", "restore" })
        {
            orders.MapPost("/{id:long}/" + action, (HttpContext h, LegacyOrders o, long id, CancellationToken ct) =>
            { RequireEdit(h, action is "complete" or "archive" or "restore"); return o.Transition(h, id, action, ct); });
        }
    }
    private static void MapNative(RouteGroupBuilder orders)
    {
        static LegacyUser Actor(HttpContext h) => (LegacyUser)h.Items["orderUser"]!;
        orders.MapGet("", (HttpContext h, NativeOrders o, CancellationToken ct, string? q = null, string? status = null,
            bool archived = false, int page = 1, int pageSize = 50) => o.Query(q, status, archived, page, pageSize, ct, Actor(h).Role is "biuro" or "technolog"));
        orders.MapGet("/lookups", (NativeOrders o, CancellationToken ct) => o.Lookups(ct));
        orders.MapGet("/questions", (NativeOrders o, CancellationToken ct, string? q = null, string? status = null, int page = 1, int pageSize = 50) =>
            o.QuestionQueue(q, status, page, pageSize, ct));
        orders.MapGet("/{id:long}", (NativeOrders o, long id, CancellationToken ct) => o.Detail(id, ct));
        orders.MapGet("/{id:long}/events", (NativeOrders o, long id, CancellationToken ct) => o.Events(id, ct));
        orders.MapGet("/{id:long}/quote", (NativeOrders o, long id, CancellationToken ct) => o.Quote(id, ct));
        orders.MapGet("/{id:long}/documents/{kind}", (HttpContext h, NativeOrders o, long id, string kind, CancellationToken ct) =>
        { if (kind == "operations") RequireEdit(h, true); return o.DownloadDocument(id, kind, ct); });
        orders.MapPost("/{id:long}/save-as-template", async (HttpContext h, NativeOrders o, long id, SaveOrderAsTemplate c, CancellationToken ct) =>
        { var template = await o.SaveAsTemplate((LegacyUser)h.Items["orderUser"]!, id, c, ct); return Results.Json(template, statusCode: 201); });
        orders.MapPost("/intake", (HttpContext h, NativeOrders o, CreateOrder c, CancellationToken ct) => o.Intake(Actor(h), c, ct));
        orders.MapPost("", (HttpContext h, NativeOrders o, CreateOrder c, CancellationToken ct) => o.Create(Actor(h), c, ct));
        orders.MapPut("/{id:long}", (HttpContext h, NativeOrders o, long id, EditOrder c, CancellationToken ct) => o.Edit(Actor(h), id, c, ct));
        orders.MapPost("/quote/preview", (NativeOrders o, OrderQuoteInput c, CancellationToken ct) => o.Preview(c, ct));
        orders.MapPost("/{id:long}/quote", (HttpContext h, NativeOrders o, long id, OrderQuoteInput c, CancellationToken ct) => o.SaveQuote(Actor(h), id, c, ct));
        orders.MapPost("/{id:long}/quote/manual", (HttpContext h, NativeOrders o, long id, OrderManualQuote c, CancellationToken ct) => o.SaveManualQuote(Actor(h), id, c, ct));
        orders.MapPost("/{id:long}/triage", (HttpContext h, NativeOrders o, long id, CancellationToken ct) => o.Triage(Actor(h), id, ct));
        orders.MapGet("/{id:long}/operations", (NativeOrders o, long id, CancellationToken ct) => o.Operations(id, ct));
        orders.MapGet("/{id:long}/attachments", (NativeOrders o, long id, CancellationToken ct) => o.Attachments(id, ct));
        orders.MapGet("/{id:long}/attachments/{attachmentId:long}/download", (NativeOrders o, long id, long attachmentId, CancellationToken ct) => o.DownloadAttachment(id, attachmentId, ct));
        orders.MapPost("/{id:long}/attachments", (HttpContext h, NativeOrders o, long id, string filename, Guid requestId, CancellationToken ct) =>
        {
            var size = h.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 100L * 1024 * 1024;
            return o.UploadAttachment(Actor(h), id, requestId, filename, h.Request.Body, h.Request.ContentLength, ct);
        });
        orders.MapPost("/{id:long}/attachments/{attachmentId:long}/remove", (HttpContext h, NativeOrders o, long id, long attachmentId, RemoveOrderAttachment c, CancellationToken ct) =>
            o.RemoveAttachment(Actor(h), id, attachmentId, c, ct));
        orders.MapPatch("/{id:long}/operations/{operationId:long}/hours", (HttpContext h, NativeOrders o, long id, long operationId, SetOrderHours c, CancellationToken ct) => o.SetHours(Actor(h), id, operationId, c, ct));
        orders.MapGet("/{id:long}/questions", (NativeOrders o, long id, CancellationToken ct) => o.Questions(id, ct));
        orders.MapPost("/{id:long}/questions", (HttpContext h, NativeOrders o, long id, AskOrderQuestion c, CancellationToken ct) => o.AskQuestion(Actor(h), id, c, ct));
        orders.MapPost("/{id:long}/questions/{questionId:long}/answer", (HttpContext h, NativeOrders o, long id, long questionId, AnswerOrderQuestion c, CancellationToken ct) => o.AnswerQuestion(Actor(h), id, questionId, c, ct));
        foreach (var action in new[] { "confirm", "complete", "deliver", "archive", "restore" })
            orders.MapPost("/{id:long}/" + action, (HttpContext h, NativeOrders o, long id, CancellationToken ct) => o.Transition(Actor(h), id, action, ct));
    }
    private static void RequireEdit(HttpContext http, bool technologOnly = false)
    {
        var user = (LegacyUser)http.Items["orderUser"]!;
        if (user.Role != "technolog" && (technologOnly || user.Role != "biuro")) throw new CrmFault(403, "Brak uprawnień do tej operacji.");
    }
}
