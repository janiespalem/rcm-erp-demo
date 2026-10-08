using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class TemplateEndpoints
{
    private static bool Native(HttpContext http) => http.RequestServices.GetRequiredService<IConfiguration>()["Catalog:Mode"] == "native";
    private static LegacyUser Actor(HttpContext http) => (LegacyUser)http.Items["templateActor"]!;
    private static void RequireNative(HttpContext http)
    { if (!Native(http)) throw new CrmFault(503, "Zapisy katalogu nie zostały przełączone na nowy serwer."); }
    private static async ValueTask<object?> Authorize(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext; var actor = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
        if (actor.Id <= 0 || actor.Role is not ("biuro" or "technolog" or "ceo")) throw new CrmFault(403, "Brak dostępu do katalogu.");
        http.Items["templateActor"] = actor; return await next(context);
    }
    public static void MapTemplateEndpoints(this WebApplication app)
    {
        var templates = app.MapGroup("/api/v1/templates"); templates.AddEndpointFilter(Authorize);
        templates.MapGet("/features", async (HttpContext h, NativeTemplates n, LegacyTemplates l, CancellationToken ct) => Native(h) ? await n.Features(ct) : await l.Features(h, ct));
        templates.MapGet("", async (HttpContext h, NativeTemplates n, LegacyTemplates l, CancellationToken ct, string? q = null, string? category = null, string? projectCode = null,
            bool includeArchived = false, int page = 1, int pageSize = 50) => Native(h) ? await n.List(q, category, projectCode, includeArchived, page, pageSize, ct) : await l.List(h, q, category, projectCode, page, pageSize, ct));
        templates.MapGet("/{id:long}", async (HttpContext h, long id, NativeTemplates n, LegacyTemplates l, CancellationToken ct) => Native(h) ? await n.Detail(id, ct) : await l.Detail(h, id, ct));
        templates.MapPost("", (HttpContext h, CreateProductTemplate c, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.Create(Actor(h), c, ct); });
        templates.MapPost("/{id:long}/update", (HttpContext h, long id, UpdateProductTemplate c, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.Update(Actor(h), id, c, ct); });
        templates.MapPost("/{id:long}/archive", (HttpContext h, long id, ProductTemplateVersionCommand c, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.SetActive(Actor(h), id, c, false, ct); });
        templates.MapPost("/{id:long}/restore", (HttpContext h, long id, ProductTemplateVersionCommand c, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.SetActive(Actor(h), id, c, true, ct); });
        templates.MapPut("/{id:long}/drawing", (HttpContext h, long id, NativeTemplates n, CancellationToken ct) =>
        {
            RequireNative(h); NativeTemplates.RequireTech(Actor(h));
            var feature = h.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 25L * 1024 * 1024;
            if (!Guid.TryParse(h.Request.Headers["X-Request-Id"], out var requestId) || !long.TryParse(h.Request.Headers["X-Expected-Version"], out var version))
                throw CrmFault.Invalid("requestId", "Podaj identyfikator zapisu i wersję szablonu.");
            return n.UploadDrawing(Actor(h), id, requestId, version, Uri.UnescapeDataString(h.Request.Headers["X-Filename"].ToString()),
                h.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant(), h.Request.Body, h.Request.ContentLength, ct);
        });
        templates.MapGet("/{id:long}/drawing", async (HttpContext h, long id, NativeTemplates n, LegacyTemplates l, CancellationToken ct) =>
            Native(h) ? await n.DownloadDrawing(id, ct) : await l.Download(h, $"api/templates/{id}/drawing", $"rysunek_{id}.pdf", true, ct));
        templates.MapPost("/{id:long}/drawing/preview", (HttpContext h, long id, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.PreviewDrawing(Actor(h), id, ct); });
        templates.MapPost("/{id:long}/drawing/apply", (HttpContext h, long id, ApplyTemplateDrawing c, NativeTemplates n, CancellationToken ct) => { RequireNative(h); return n.ApplyDrawing(Actor(h), id, c, ct); });
        templates.MapGet("/{id:long}/arkusz", async (HttpContext h, long id, NativeTemplates n, LegacyTemplates l, CancellationToken ct) =>
        { NativeTemplates.RequireTech(Actor(h)); return Native(h) ? await n.Worksheet(Actor(h), id, ct) : await l.Download(h, $"api/templates/{id}/arkusz", $"{id}_arkusz.pdf", false, ct); });
        var projects = app.MapGroup("/api/v1/projects"); projects.AddEndpointFilter(Authorize);
        projects.MapGet("", async (HttpContext h, NativeTemplates n, LegacyTemplates l, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) =>
            Native(h) ? await n.Projects(q, page, pageSize, ct) : await l.Projects(h, q, page, pageSize, ct));
        projects.MapGet("/{code}/arkusze", async (HttpContext h, string code, NativeTemplates n, LegacyTemplates l, CancellationToken ct) =>
        {
            NativeTemplates.RequireTech(Actor(h));
            if (string.IsNullOrEmpty(code) || code.Length > 50) throw CrmFault.Invalid("projectCode", "Podaj kod projektu (do 50 znaków).");
            return Native(h) ? await n.ProjectWorksheets(Actor(h), code, ct) : await l.Download(h, $"api/projects/{Uri.EscapeDataString(code)}/arkusze", OrderDocumentRenderer.SafeFilename(code, "projekt") + "_arkusze.pdf", false, ct);
        });
    }
}
