using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class CatalogEndpoints
{
    private static bool Native(HttpContext http) => http.RequestServices.GetRequiredService<IConfiguration>()["Catalog:Mode"] == "native";
    private static LegacyUser Actor(HttpContext http) => (LegacyUser)http.Items["catalogActor"]!;
    private static void RequireNative(HttpContext http)
    { if (!Native(http)) throw new CrmFault(503, "Zapisy katalogu nie zostały przełączone na nowy serwer."); }
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var catalog = app.MapGroup("/api/v1/catalog");
        catalog.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var actor = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            if (actor.Id <= 0 || actor.Role is not ("biuro" or "technolog" or "ceo")) throw new CrmFault(403, "Brak dostępu do katalogu.");
            http.Items["catalogActor"] = actor;
            return await next(context);
        });
        catalog.MapGet("/features", async (HttpContext http, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct) => Native(http) ? await native.Features(ct) : await legacy.Features(http, ct));
        catalog.MapGet("/materials", async (HttpContext http, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct, string? q = null, bool includeArchived = false, int page = 1, int pageSize = 50)
            => Native(http) ? await native.Materials(q, includeArchived, page, pageSize, ct) : await legacy.Materials(http, q, page, pageSize, ct));
        catalog.MapGet("/operations", async (HttpContext http, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50)
            => Native(http) ? await native.Operations(q, page, pageSize, ct) : await legacy.Operations(http, q, page, pageSize, ct));
        catalog.MapGet("/materials/{id:long}", async (HttpContext http, long id, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct) => Native(http) ? await native.Material(id, ct) : await legacy.Material(http, id, ct));
        catalog.MapGet("/operations/{id:long}", async (HttpContext http, long id, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct) => Native(http) ? await native.Operation(id, ct) : await legacy.Operation(http, id, ct));
        catalog.MapGet("/operations/suggest", async (HttpContext http, NativeCatalog native, LegacyCatalog legacy, CancellationToken ct, string? text = null, string? material = null) => Native(http) ? await native.Suggest(text, material, ct) : await legacy.Suggest(http, text, material, ct));
        catalog.MapPost("/materials", async (HttpContext http, CreateCatalogMaterial c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.CreateMaterial(Actor(http), c, ct); });
        catalog.MapPost("/materials/{id:long}/update", async (HttpContext http, long id, UpdateCatalogMaterial c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.UpdateMaterial(Actor(http), id, c, ct); });
        catalog.MapPost("/materials/{id:long}/archive", async (HttpContext http, long id, CatalogVersionCommand c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.Remove(Actor(http), id, c, true, ct); });
        catalog.MapPost("/operations", async (HttpContext http, CreateCatalogOperation c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.CreateOperation(Actor(http), c, ct); });
        catalog.MapPost("/operations/{id:long}/update", async (HttpContext http, long id, UpdateCatalogOperation c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.UpdateOperation(Actor(http), id, c, ct); });
        catalog.MapPost("/operations/{id:long}/delete", async (HttpContext http, long id, CatalogVersionCommand c, NativeCatalog native, CancellationToken ct) => { RequireNative(http); return await native.Remove(Actor(http), id, c, false, ct); });
    }
}
