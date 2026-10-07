using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class InsightsEndpoints
{
    private static bool Native(HttpContext h) => h.RequestServices.GetRequiredService<IConfiguration>()["Orders:Mode"] == "native";
    public static void MapInsightsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/insights");
        group.AddEndpointFilter(async (context, next) =>
        {
            var h = context.HttpContext; var actor = await h.RequestServices.GetRequiredService<IIdentity>().Validate(h.Request.Headers.Authorization, h.RequestAborted);
            var history = h.Request.Path.Value?.TrimEnd('/') is "/api/v1/insights/service-history" or "/api/v1/insights/features";
            if (actor.Id <= 0 || actor.Role is not ("technolog" or "ceo") && !(history && actor.Role == "biuro")) throw new CrmFault(403, "Brak dostępu do raportów.");
            h.Items["insightActor"] = actor; return await next(context);
        });
        group.MapGet("/features", (HttpContext h) =>
        {
            var management = ((LegacyUser)h.Items["insightActor"]!).Role is "technolog" or "ceo";
            return new InsightFeatures(management, management, management, management, management, true, management, Native(h));
        });
        group.MapGet("/analytics", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct) => Native(h) ? await n.Analytics(ct) : await l.Analytics(h, ct));
        group.MapGet("/production", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) => Native(h) ? await n.Production(q, page, pageSize, ct) : await l.Production(h, q, page, pageSize, ct));
        group.MapGet("/schedule", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) => Native(h) ? await n.Schedule(q, page, pageSize, ct) : await l.Schedule(h, q, page, pageSize, ct));
        group.MapGet("/profitability", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) => Native(h) ? await n.Profitability(q, page, pageSize, ct) : await l.Profitability(h, q, page, pageSize, ct));
        group.MapGet("/benchmark", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct, string? material = null, string? orderType = null, int page = 1, int pageSize = 50) => Native(h) ? await n.Benchmark(material, orderType, page, pageSize, ct) : await l.Benchmark(h, material, orderType, page, pageSize, ct));
        group.MapGet("/service-history", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) => Native(h) ? await n.ServiceHistory(q, page, pageSize, ct) : await l.ServiceHistory(h, q, page, pageSize, ct));
        group.MapGet("/export.xlsx", async (HttpContext h, NativeInsights n, LegacyInsights l, CancellationToken ct) => Native(h) ? await n.Export(ct) : await l.Export(h, ct));
    }
}
