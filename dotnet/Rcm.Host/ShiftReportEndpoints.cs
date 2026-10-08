using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class ShiftReportEndpoints
{
    private static bool Native(HttpContext http) => http.RequestServices.GetRequiredService<IConfiguration>()["ShiftReports:Mode"] == "native";
    private static LegacyUser Actor(HttpContext http) => (LegacyUser)http.Items["shiftReportsActor"]!;
    private static void RequireNative(HttpContext http)
    { if (!Native(http)) throw new CrmFault(503, "Zapisy raportów zmianowych nie zostały przełączone."); }
    public static void MapShiftReportEndpoints(this WebApplication app)
    {
        var reports = app.MapGroup("/api/v1/shift-reports");
        reports.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var actor = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            NativeShiftReports.RequireRead(actor); http.Items["shiftReportsActor"] = actor;
            return await next(context);
        });
        reports.MapGet("/features", async (HttpContext h, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct) => Native(h) ? await n.Features(Actor(h), ct) : l.Features(Actor(h)));
        reports.MapGet("", async (HttpContext h, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct,
            DateOnly? dateFrom = null, DateOnly? dateTo = null, string? shift = null, bool deleted = false, int page = 1, int pageSize = 50) =>
            Native(h) ? await n.List(Actor(h), dateFrom, dateTo, shift, deleted, page, pageSize, ct) : await l.List(h, Actor(h), dateFrom, dateTo, shift, deleted, page, pageSize, ct));
        reports.MapGet("/today", async (HttpContext h, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct) => Native(h) ? await n.Today(Actor(h), ct) : await l.Today(h, Actor(h), ct));
        reports.MapGet("/summary", async (HttpContext h, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? shift = null) =>
            Native(h) ? await n.Summary(Actor(h), dateFrom, dateTo, shift, ct) : await l.Summary(h, Actor(h), dateFrom, dateTo, shift, ct));
        reports.MapGet("/{id:long}", async (HttpContext h, long id, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct) => Native(h) ? await n.Detail(Actor(h), id, ct) : await l.Detail(h, Actor(h), id, ct));
        reports.MapGet("/{id:long}/audit", async (HttpContext h, long id, NativeShiftReports n, LegacyShiftReports l, CancellationToken ct, bool includeDrafts = false) =>
            Native(h) ? await n.Audit(Actor(h), id, includeDrafts, ct) : await l.Audit(h, Actor(h), id, includeDrafts, ct));
        reports.MapPost("", (HttpContext h, CreateShiftReport c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.Create(Actor(h), c, ct); });
        reports.MapPost("/{id:long}/save", (HttpContext h, long id, SaveShiftReport c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.Save(Actor(h), id, c, ct); });
        reports.MapPost("/{id:long}/finalize", (HttpContext h, long id, ShiftReportVersionCommand c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.Finalize(Actor(h), id, c, ct); });
        reports.MapPost("/{id:long}/corrections", (HttpContext h, long id, CorrectShiftReport c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.Correct(Actor(h), id, c, ct); });
        reports.MapPost("/{id:long}/discard", (HttpContext h, long id, ShiftReportVersionCommand c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.Discard(Actor(h), id, c, ct); });
        reports.MapPost("/{id:long}/admin-delete", (HttpContext h, long id, AdminDeleteShiftReport c, NativeShiftReports n, CancellationToken ct) => { RequireNative(h); return n.AdminDelete(Actor(h), id, c, ct); });
    }
}
