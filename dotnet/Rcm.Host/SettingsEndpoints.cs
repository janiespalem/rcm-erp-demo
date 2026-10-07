using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class SettingsEndpoints
{
    private static bool Native(HttpContext http) => http.RequestServices.GetRequiredService<IConfiguration>()["Catalog:Mode"] == "native";
    private static LegacyUser Actor(HttpContext http) => (LegacyUser)http.Items["settingsActor"]!;
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var settings = app.MapGroup("/api/v1/settings");
        settings.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var actor = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            NativeSettings.RequireRead(actor); http.Items["settingsActor"] = actor; return await next(context);
        });
        settings.MapGet("", async (HttpContext h, NativeSettings n, LegacySettings l, CancellationToken ct) => Native(h) ? await n.List(Actor(h), ct) : await l.List(h, Actor(h), ct));
        settings.MapGet("/features", async (HttpContext h, NativeSettings n, LegacySettings l, CancellationToken ct) => Native(h) ? await n.Features(Actor(h), ct) : l.Features(Actor(h)));
        settings.MapPost("/{key}", (HttpContext h, string key, UpdateSetting command, NativeSettings n, CancellationToken ct) =>
        {
            if (!Native(h)) throw new CrmFault(503, "Zapisy katalogu nie zostały przełączone.");
            return n.Update(Actor(h), key, command, ct);
        });
    }
}
