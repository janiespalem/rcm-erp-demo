using Microsoft.EntityFrameworkCore;
using Rcm.Calculators;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/session", async (HttpContext http, IIdentity identity, CrmDb db, CancellationToken ct) =>
        {
            var user = await identity.Validate(http.Request.Headers.Authorization, ct);
            if (user.Role is not ("biuro" or "technolog" or "ceo" or "produkcja" or "crm"))
                throw new CrmFault(403, "Brak dostępu do aplikacji.");
            var member = await db.Memberships.AsNoTracking().Include(m => m.Team).SingleOrDefaultAsync(m => m.UserId == user.Id, ct);
            if (user.Role == "crm" && member is null) throw new CrmFault(403, "Brak przypisanego dostępu do CRM.");
            return new SessionDto(user.Id, user.Name, member?.TeamId, member?.Team.Name, user.Role,
                ProductionEnabled: !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Production")));
        });

        var calculators = app.MapGroup("/api/v1/calculators");
        calculators.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var user = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
            if (user.Role is not ("biuro" or "technolog")) throw new CrmFault(403, "Brak dostępu do kalkulatorów.");
            return await next(context);
        });
        calculators.MapPost("/tetrapod", (TetrapodInput input) =>
        {
            try { return TetrapodCalculator.Calculate(input); }
            catch (ArgumentException error)
            {
                throw CrmFault.Invalid(error.ParamName ?? "form", error.ParamName == "deliveriesKg"
                    ? "Masa dostawy musi być nieujemną liczbą."
                    : "Podaj pełne, nieujemne liczby w zakresie obliczeń. Wykonanie nie może przekraczać planu.");
            }
        });
        calculators.MapGet("/lego/catalog", () => new { series = LegoCalculator.Series, products = LegoCalculator.Products });
        calculators.MapPost("/lego", (LegoInput input, CancellationToken ct) =>
        {
            try { return LegoCalculator.Calculate(input, ct); }
            catch (ArgumentException error) { throw CrmFault.Invalid("form", error.Message); }
        });
    }
}
