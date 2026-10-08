using Rcm.Contracts;
using Rcm.Production;

namespace Rcm.Host;

internal static class ProductionEndpoints
{
    private static ProductionActor Actor(HttpContext http) => (ProductionActor)http.Items["productionActor"]!;
    private static LegacyUser ReviewActor(HttpContext http) => (LegacyUser)http.Items["productionIdentity"]!;
    private static NativeShiftReports Reviews(HttpContext http)
    {
        if (!Enabled(http)) throw new ProductionFault(503, "Moduł produkcji nie jest jeszcze dostępny.");
        return http.RequestServices.GetRequiredService<NativeShiftReports>();
    }
    private static bool Enabled(HttpContext http) => !string.IsNullOrWhiteSpace(http.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Production"));
    private static ProductionModule Module(HttpContext http)
    {
        if (!Enabled(http)) throw new ProductionFault(503, "Moduł produkcji nie jest jeszcze dostępny.");
        return http.RequestServices.GetRequiredService<ProductionModule>();
    }
    public static void MapProductionEndpoints(this WebApplication app)
    {
        var production = app.MapGroup("/api/v1/production");
        production.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var user = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            var actor = new ProductionActor(user.Id, user.Role);
            if (!ProductionModule.CanRead(actor)) throw new ProductionFault(403, "Brak dostępu do modułu produkcji.");
            http.Items["productionActor"] = actor;
            http.Items["productionIdentity"] = user;
            return await next(context);
        });
        production.MapGet("/features", async (HttpContext http, CancellationToken ct) =>
        {
            if (Enabled(http)) return await Module(http).Features(Actor(http), ct);
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime);
            return new ProductionFeatures(false, false, today);
        });
        production.MapGet("/contracts", (HttpContext http, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50)
            => Module(http).Contracts(Actor(http), q, page, pageSize, ct));
        production.MapPost("/contracts", (HttpContext http, CreateProductionContract command, CancellationToken ct)
            => Module(http).Create(Actor(http), command, ct));
        production.MapGet("/contracts/{id:guid}", (HttpContext http, Guid id, CancellationToken ct)
            => Module(http).Contract(Actor(http), id, ct));
        production.MapPut("/contracts/{id:guid}", (HttpContext http, Guid id, SaveProductionContract command, CancellationToken ct)
            => Module(http).Save(Actor(http), id, command, ct));
        production.MapGet("/contracts/{id:guid}/deliveries", (HttpContext http, Guid id, CancellationToken ct, int page = 1, int pageSize = 50)
            => Module(http).Deliveries(Actor(http), id, page, pageSize, ct));
        production.MapPost("/contracts/{id:guid}/deliveries", (HttpContext http, Guid id, CreateSteelDelivery command, CancellationToken ct)
            => Module(http).AddDelivery(Actor(http), id, command, ct));
        production.MapGet("/contracts/{id:guid}/deliveries/{deliveryId:guid}", (HttpContext http, Guid id, Guid deliveryId, CancellationToken ct)
            => Module(http).Delivery(Actor(http), id, deliveryId, ct));
        production.MapPost("/contracts/{id:guid}/deliveries/{deliveryId:guid}", (HttpContext http, Guid id, Guid deliveryId, CorrectSteelDelivery command, CancellationToken ct)
            => Module(http).CorrectDelivery(Actor(http), id, deliveryId, command, ct));
        production.MapGet("/contracts/{id:guid}/audit", (HttpContext http, Guid id, CancellationToken ct, int page = 1, int pageSize = 50)
            => Module(http).Audit(Actor(http), id, page, pageSize, ct));
        production.MapGet("/review/features", async (HttpContext http, CancellationToken ct) =>
        {
            var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
            if (!Enabled(http) || configuration["ShiftReports:Mode"] != "native") return new ProductionReviewFeatures(false, false, false);
            return await Reviews(http).ProductionReviewFeatures(ReviewActor(http), ct);
        });
        production.MapGet("/reports/{id:long}", (HttpContext http, long id, CancellationToken ct)
            => Reviews(http).ProductionReport(ReviewActor(http), id, ct));
        production.MapPost("/reports/{id:long}/link", (HttpContext http, long id, LinkProductionReport command, CancellationToken ct)
            => Reviews(http).LinkProductionReport(ReviewActor(http), id, command, ct));
        production.MapPost("/reports/{id:long}/review", (HttpContext http, long id, ReviewProductionReport command, CancellationToken ct)
            => Reviews(http).ReviewProductionReport(ReviewActor(http), id, command, ct));
        production.MapGet("/review/queue", (HttpContext http, CancellationToken ct, Guid? contractId = null, string state = "pending", int page = 1, int pageSize = 50)
            => Reviews(http).ProductionReviewQueue(ReviewActor(http), contractId, state, page, pageSize, ct));
        production.MapGet("/contracts/{id:guid}/accepted-operations", (HttpContext http, Guid id, CancellationToken ct)
            => Reviews(http).ProductionAcceptedTotals(ReviewActor(http), id, ct));
        production.MapGet("/contracts/{id:guid}/reports", (HttpContext http, Guid id, CancellationToken ct, string state = "all", int page = 1, int pageSize = 50)
            => Reviews(http).ProductionContractReports(ReviewActor(http), id, state, page, pageSize, ct));
        production.MapGet("/reports/{id:long}/audit", (HttpContext http, long id, CancellationToken ct, int page = 1, int pageSize = 50)
            => Reviews(http).ProductionReportAudit(ReviewActor(http), id, page, pageSize, ct));
    }
}
