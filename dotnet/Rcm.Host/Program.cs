using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;
using Rcm.Host;
using Rcm.Production;

var administrative = args.Any(a => a is "--migrate" or "--migrate-production" or "--grant-user" or "--revoke-user" or "--set-password" or "--revoke-password" or "--list-login-users");
var builder = WebApplication.CreateBuilder(administrative ? [] : args);
if (builder.Configuration["Orders:Mode"] is { } ordersMode && ordersMode is not ("legacy" or "native"))
    throw new InvalidOperationException("Orders:Mode must be legacy or native.");
if (builder.Configuration["Catalog:Mode"] is { } catalogMode && catalogMode is not ("legacy" or "native"))
    throw new InvalidOperationException("Catalog:Mode must be legacy or native.");
if (builder.Configuration["ShiftReports:Mode"] is { } shiftMode && shiftMode is not ("legacy" or "native"))
    throw new InvalidOperationException("ShiftReports:Mode must be legacy or native.");
if (builder.Configuration["Identity:Mode"] is { } identityMode && identityMode is not ("legacy" or "native"))
    throw new InvalidOperationException("Identity:Mode must be legacy or native.");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 65536);
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddDbContext<CrmDb>((sp, o) => o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Crm")
    ?? throw new InvalidOperationException("ConnectionStrings__Crm is required."), p => p.MigrationsHistoryTable("__EFMigrationsHistory", "crm")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CrmModule>();
builder.Services.AddDbContext<ProductionDb>((sp, o) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connection = args.Contains("--migrate-production")
        ? configuration.GetConnectionString("ProductionAdmin")
            ?? throw new InvalidOperationException("ConnectionStrings__ProductionAdmin is required for production migrations.")
        : configuration.GetConnectionString("Production")
            ?? throw new InvalidOperationException("Production is not configured.");
    o.UseNpgsql(connection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "production"));
});
builder.Services.AddScoped<ProductionModule>();
builder.Services.AddScoped<LegacyIdentity>();
builder.Services.AddScoped<NativeIdentity>();
builder.Services.AddSingleton<LoginAttempts>();
builder.Services.AddScoped<IIdentity>(sp => sp.GetRequiredService<IConfiguration>()["Identity:Mode"] == "native"
    ? sp.GetRequiredService<NativeIdentity>() : sp.GetRequiredService<LegacyIdentity>());
builder.Services.AddScoped<LegacyOrders>();
builder.Services.AddScoped<NativeOrders>();
builder.Services.AddScoped<NativeCatalog>();
builder.Services.AddScoped<LegacyCatalog>();
builder.Services.AddScoped<NativeTemplates>();
builder.Services.AddScoped<LegacyTemplates>();
builder.Services.AddScoped<NativeShiftReports>();
builder.Services.AddScoped<LegacyShiftReports>();
builder.Services.AddScoped<NativeInsights>();
builder.Services.AddScoped<LegacyInsights>();
builder.Services.AddScoped<NativeSettings>();
builder.Services.AddScoped<LegacySettings>();
builder.Services.AddHttpClient("legacy", (sp, c) =>
{
    c.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()["Legacy:BaseUrl"] ?? "http://127.0.0.1:9/");
    c.Timeout = TimeSpan.FromSeconds(8);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("legacy-documents", (sp, c) =>
{
    c.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()["Legacy:BaseUrl"] ?? "http://127.0.0.1:9/");
    c.Timeout = TimeSpan.FromMinutes(2);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
var productionEnabled = !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Production"));
if (args.Contains("--migrate-production"))
{
    if (args.Length != 1) throw new InvalidOperationException("Run the production migration command separately.");
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ProductionDb>().Database.MigrateAsync();
    return;
}
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CrmDb>().Database.MigrateAsync();
    return;
}
if (args.Contains("--grant-user") || args.Contains("--revoke-user"))
{
    await Provisioning.Run(app.Services, args);
    return;
}
if (args.Any(a => a is "--set-password" or "--revoke-password" or "--list-login-users"))
{
    await IdentityProvisioning.Run(app.Services, args);
    return;
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (CrmFault e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new ApiError(e.Status, e.Message, e.Errors)); }
    catch (ProductionFault e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new ApiError(e.Status, e.Message, e.Errors)); }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 422;
        await context.Response.WriteAsJsonAsync(new ApiError(422, "Nieprawidłowy format danych.", new() { ["form"] = ["Sprawdź wymagane pola i format daty."] }));
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e) when (e is NpgsqlException or DbUpdateException)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ApiError(503, "Nie można potwierdzić zapisu. Zachowaj formularz i ponów ten sam zapis."));
    }
});
app.MapDesktopDownloads();
app.MapApplicationEndpoints();
app.MapIdentityCompatibility();
app.MapOrderEndpoints();
app.MapCatalogEndpoints();
app.MapTemplateEndpoints();
app.MapShiftReportEndpoints();
app.MapInsightsEndpoints();
app.MapSettingsEndpoints();
app.MapProductionEndpoints();
app.MapGet("/health", async (CrmDb db, NativeOrders orders, NativeCatalog catalog, NativeTemplates templates, NativeShiftReports shifts, NativeSettings settings, CancellationToken ct) =>
{
    await db.Memberships.AsNoTracking().AnyAsync(ct);
    var mode = app.Configuration["Orders:Mode"] ?? "legacy";
    if (mode == "native") await orders.CheckRuntime(ct);
    var catalogMode = app.Configuration["Catalog:Mode"] ?? "legacy";
    if (catalogMode == "native") { await catalog.CheckRuntime(ct); await templates.CheckRuntime(ct); await settings.CheckRuntime(ct); }
    var shiftReportsMode = app.Configuration["ShiftReports:Mode"] ?? "legacy";
    if (shiftReportsMode == "native") await shifts.CheckRuntime(ct);
    var identityMode = app.Configuration["Identity:Mode"] ?? "legacy";
    if (identityMode == "native")
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NativeIdentity>().CheckRuntime(ct);
    }
    if (productionEnabled)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ProductionModule>().CheckRuntime(ct);
        if (shiftReportsMode == "native") await shifts.CheckProductionReviewRuntime(ct);
    }
    return Results.Ok(new { status = "ok", identityMode, identityNativeReady = identityMode == "native", archiveAware = true, ordersMode = mode, ordersNativeReady = mode == "native", catalogMode, catalogNativeReady = catalogMode == "native",
        shiftReportsMode, shiftReportsNativeReady = shiftReportsMode == "native", productionEnabled, productionReady = productionEnabled,
        productionReviewReady = productionEnabled && shiftReportsMode == "native",
        commit = Environment.GetEnvironmentVariable("DEPLOYED_COMMIT") ?? "development" });
});
app.MapPost("/api/v1/session/login", async (HttpContext http, LoginRequest login, IIdentity identity, LoginAttempts attempts, CancellationToken ct) =>
{
    if (app.Configuration["Identity:AllowPinLogin"] != "true") return Results.NotFound();
    return Results.Ok(await attempts.Authenticate(http, "pin:" + login.Role, () => identity.Login(login, ct)));
});
app.MapPost("/api/v1/session/login/password", async (HttpContext http, PasswordLoginRequest login, IIdentity identity, LoginAttempts attempts, CancellationToken ct) =>
{
    string principal;
    try { principal = NativePasswords.NormalizeUsername(login.Username); }
    catch (ArgumentException) { principal = "invalid"; }
    return Results.Ok(await attempts.Authenticate(http, "password:" + principal, () => identity.LoginWithPassword(login, ct)));
});
app.MapPost("/api/v1/session/refresh", async (SessionRefreshRequest refresh, IIdentity identity, CancellationToken ct) => Results.Ok(await identity.Refresh(refresh, ct)));
app.MapPost("/api/v1/session/logout", async (SessionRefreshRequest refresh, IIdentity identity, CancellationToken ct) =>
{
    await identity.Logout(refresh, ct);
    return Results.NoContent();
});
var crm = app.MapGroup("/api/v1/crm");
crm.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var user = await http.RequestServices.GetRequiredService<IIdentity>().Validate(http.Request.Headers.Authorization, http.RequestAborted);
    var member = await http.RequestServices.GetRequiredService<CrmDb>().Memberships.AsNoTracking().Include(m => m.Team).SingleOrDefaultAsync(m => m.UserId == user.Id, http.RequestAborted)
        ?? throw new CrmFault(403, "Brak przypisanego dostępu do CRM.");
    http.Items["crmActor"] = new CrmActor(user.Id, user.Name, member.TeamId);
    http.Items["crmSession"] = new SessionDto(user.Id, user.Name, member.TeamId, member.Team.Name, user.Role);
    return await next(context);
});
static CrmActor Actor(HttpContext c) => (CrmActor)c.Items["crmActor"]!;
crm.MapGet("/session", (HttpContext c) => c.Items["crmSession"]);
crm.MapGet("/customers", (HttpContext c, CrmModule crm, CancellationToken ct, string? q = null, string? product = null, string? state = null, string? due = null, int page = 1, int pageSize = 50) => crm.Query(Actor(c), q, product, state, due, page, pageSize, ct));
crm.MapGet("/customers/summaries", (HttpContext c, CrmModule crm, CancellationToken ct, string? q = null, string? product = null, string? state = null, string? due = null, int page = 1, int pageSize = 50) => crm.Summaries(Actor(c), q, product, state, due, page, pageSize, ct));
crm.MapGet("/customers/archived", (HttpContext c, CrmModule crm, CancellationToken ct, string? q = null, int page = 1, int pageSize = 50) => crm.Archived(Actor(c), q, page, pageSize, ct));
crm.MapPost("/customers", (HttpContext c, CrmModule crm, CreateCustomer command, CancellationToken ct) => crm.Create(Actor(c), command, ct));
crm.MapGet("/customers/{id:guid}", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct) => crm.Detail(Actor(c), id, ct));
crm.MapGet("/customers/{id:guid}/record", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct) => crm.CustomerRecord(Actor(c), id, ct));
crm.MapGet("/customers/{id:guid}/topics", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct, int page = 1, int pageSize = 50) => crm.CustomerTopics(Actor(c), id, page, pageSize, ct));
crm.MapGet("/customers/{id:guid}/history", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct, int page = 1, int pageSize = 50) => crm.CustomerHistory(Actor(c), id, page, pageSize, ct));
crm.MapPut("/customers/{id:guid}", (HttpContext c, CrmModule crm, Guid id, EditCustomer command, CancellationToken ct) => crm.Edit(Actor(c), id, command, ct));
crm.MapPost("/customers/{id:guid}/archive", (HttpContext c, CrmModule crm, Guid id, ChangeCustomerLifecycle command, CancellationToken ct) => crm.Archive(Actor(c), id, command, ct));
crm.MapPost("/customers/{id:guid}/restore", (HttpContext c, CrmModule crm, Guid id, ChangeCustomerLifecycle command, CancellationToken ct) => crm.Restore(Actor(c), id, command, ct));
crm.MapPost("/customers/{id:guid}/topics", (HttpContext c, CrmModule crm, Guid id, CreateTopic command, CancellationToken ct) => crm.CreateTopic(Actor(c), id, command, ct));
crm.MapPut("/topics/{id:guid}", (HttpContext c, CrmModule crm, Guid id, EditTopic command, CancellationToken ct) => crm.EditTopic(Actor(c), id, command, ct));
crm.MapGet("/topics/{id:guid}", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct) => crm.TopicRecord(Actor(c), id, ct));
crm.MapPost("/topics/{id:guid}/contacts", (HttpContext c, CrmModule crm, Guid id, RecordContact command, CancellationToken ct) => crm.Record(Actor(c), id, command, ct));
crm.MapGet("/topics/{id:guid}/history", (HttpContext c, CrmModule crm, Guid id, CancellationToken ct, int page = 1, int pageSize = 50) => crm.History(Actor(c), id, page, pageSize, ct));
crm.MapGet("/queue", (HttpContext c, CrmModule crm, CancellationToken ct, string group = "today", int page = 1, int pageSize = 50) => crm.Queue(Actor(c), group, page, pageSize, ct));
app.Run();
public partial class Program;
