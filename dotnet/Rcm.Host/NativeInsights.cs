using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeInsights(IConfiguration configuration, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static NpgsqlParameter P(string name, object? value) => value is null ? new(name, NpgsqlDbType.Unknown) { Value = DBNull.Value } : new(name, value);
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("Orders") ?? throw new CrmFault(503, "Raporty nie zostały skonfigurowane."));
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] args)
    {
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(args);
        await using var reader = await command.ExecuteReaderAsync(ct); List<T> rows = [];
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return rows.ToArray();
    }
    internal static void Validate(string? q, int page, int pageSize)
    { if (q?.Length > 200 || page is < 1 or > 1_000_000 || pageSize is < 1 or > 100) throw CrmFault.Invalid("page", "Nieprawidłowy filtr lub strona raportu (od 1 do 100 pozycji)."); }
    private const string Search = "(@q='' OR strpos(lower(coalesce(o.order_number,'') || ' ' || o.client || ' ' || coalesce(o.description,'')),lower(@q))>0)";
    public async Task<Page<ProductionInsight>> Production(string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize); await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var from = " FROM public.orders o LEFT JOIN public.quotes qt ON qt.order_id=o.id WHERE o.status::text IN ('in_production','gotowe') AND " + Search;
        var total = (await Read<long>(db, "SELECT to_json(count(*))" + from, ct, P("q", q?.Trim() ?? "")))[0];
        var rows = await Read<ProductionRow>(db, """
            SELECT json_build_object('id',o.id,'orderNumber',o.order_number,'client',o.client,'status',o.status,'deadline',o.deadline,
              'description',o.description,'material',o.material,'processes',coalesce(qt.processes_json,'[]'::json),'totalNet',nullif(qt.total_net,0))
            """ + from + " ORDER BY o.deadline ASC NULLS LAST,o.id DESC LIMIT @limit OFFSET @offset", ct, P("q", q?.Trim() ?? ""), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        await tx.CommitAsync(ct);
        return new(rows.Select(r => new ProductionInsight(r.Id, r.OrderNumber, r.Client, r.Status, r.Deadline, r.Description, r.Material, InsightCalculations.Routing(r.Processes), r.TotalNet)).ToArray(), checked((int)total), page, pageSize);
    }
    private const string ScheduleJson = "json_build_object('id',o.id,'orderNumber',o.order_number,'client',o.client,'status',o.status,'deadline',o.deadline,'branch',o.triage_branch)";
    public async Task<Page<ScheduleInsight>> Schedule(string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize); await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var from = " FROM public.orders o WHERE o.status::text<>'rejected' AND " + Search;
        var total = (await Read<long>(db, "SELECT to_json(count(*))" + from, ct, P("q", q?.Trim() ?? "")))[0];
        var rows = await Read<ScheduleInsight>(db, "SELECT " + ScheduleJson + from + " ORDER BY o.deadline ASC NULLS LAST,o.id LIMIT @limit OFFSET @offset", ct,
            P("q", q?.Trim() ?? ""), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        await tx.CommitAsync(ct); return new(rows, checked((int)total), page, pageSize);
    }
    private const string Hours = """
        coalesce((SELECT sum(CASE WHEN p->>'hours' ~ '^[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)([eE][+-]?[0-9]+)?$'
          THEN (p->>'hours')::double precision ELSE 0 END)
          FROM jsonb_array_elements(CASE WHEN jsonb_typeof(qt.processes_json::jsonb)='array' THEN qt.processes_json::jsonb ELSE '[]'::jsonb END) p),0)
        """;
    private const string ActualHours = "coalesce(ah.actual,0)";
    public async Task<Page<ProfitabilityInsight>> Profitability(string? q, int page, int pageSize, CancellationToken ct)
    {
        Validate(q, page, pageSize); await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var setting = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='labor_rate_pln'", ct)).SingleOrDefault();
        var rate = double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out var labor) && double.IsFinite(labor) ? labor : 100;
        var cte = "WITH recent AS (SELECT * FROM public.orders WHERE status::text IN ('in_production','gotowe','wydane') ORDER BY created_at DESC,id DESC LIMIT 50),actuals AS (SELECT order_id,sum(actual_hours)::double precision AS actual FROM public.order_operations WHERE order_id IN (SELECT id FROM recent) GROUP BY order_id) ";
        var from = " FROM recent o JOIN public.quotes qt ON qt.order_id=o.id LEFT JOIN actuals ah ON ah.order_id=o.id WHERE " + Search;
        var total = (await Read<long>(db, cte + "SELECT to_json(count(*))" + from, ct, P("q", q?.Trim() ?? "")))[0];
        var rows = await Read<ProfitRow>(db, cte + """
            SELECT json_build_object('id',o.id,'orderNumber',coalesce(nullif(o.order_number,''),'ZW-' || o.id),'client',o.client,'status',o.status,
              'price',coalesce(qt.total_net,0),'weight',coalesce(qt.material_weight_kg,0),'weightRate',coalesce(qt.material_price_per_kg,0),
              'material',coalesce(qt.material_cost,0),'planned',coalesce(qt.labor_hours,0)+
            """ + Hours + ",'actual'," + ActualHours + ")" + from + " ORDER BY o.created_at DESC,o.id DESC LIMIT @limit OFFSET @offset", ct,
            P("q", q?.Trim() ?? ""), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        await tx.CommitAsync(ct);
        return new(rows.Select(r => InsightCalculations.Profit(r.Id, r.OrderNumber, r.Client, r.Status, r.Price, r.Weight, r.WeightRate, r.Material, r.Planned, r.Actual, rate)).ToArray(), checked((int)total), page, pageSize);
    }
    public async Task<AnalyticsInsight> Analytics(CancellationToken ct)
    {
        var today = Today;
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var counts = (await Read<Counts>(db, """
            SELECT json_build_object('total',count(*),'rejected',count(*) FILTER(WHERE triage_branch='odrzut'),
              'standard',count(*) FILTER(WHERE triage_branch='standard'),'custom',count(*) FILTER(WHERE triage_branch='niestandard'),
              'inProduction',count(*) FILTER(WHERE status::text IN ('in_production','gotowe')),'done',count(*) FILTER(WHERE status::text='wydane'),
              'margin',(SELECT avg(margin_pct)::double precision FROM public.quotes),
              'cycle',avg(extract(epoch FROM delivered_at-created_at)/86400) FILTER(WHERE delivered_at IS NOT NULL AND created_at IS NOT NULL),
              'quoteStart',avg(extract(epoch FROM started_at-quoted_at)/86400) FILTER(WHERE delivered_at IS NOT NULL AND created_at IS NOT NULL))
            FROM public.orders
            """, ct))[0];
        var accuracy = (await Read<double?>(db, "WITH actuals AS (SELECT order_id,sum(actual_hours)::double precision AS actual FROM public.order_operations GROUP BY order_id),samples AS (SELECT " + ActualHours + " AS actual,coalesce(qt.labor_hours,0)+" + Hours + " AS planned FROM public.orders o JOIN public.quotes qt ON qt.order_id=o.id LEFT JOIN actuals ah ON ah.order_id=o.id WHERE o.delivered_at IS NOT NULL AND o.created_at IS NOT NULL) SELECT coalesce(to_json(avg(actual/planned*100)),'null'::json) FROM samples WHERE planned>0 AND actual>0", ct))[0];
        var revenue = await Read<RevenueMonthInsight>(db, """
            SELECT json_build_object('month',to_char(coalesce(o.created_at,@today::timestamp),'YYYY-MM'),'orders',count(*),'revenuePln',coalesce(sum(qt.total_net),0))
            FROM public.orders o LEFT JOIN public.quotes qt ON qt.order_id=o.id WHERE o.status::text IN ('gotowe','wydane','in_production')
              AND (o.created_at IS NULL OR o.created_at>=@cutoff)
            GROUP BY to_char(coalesce(o.created_at,@today::timestamp),'YYYY-MM') ORDER BY to_char(coalesce(o.created_at,@today::timestamp),'YYYY-MM')
            """, ct, P("today", today), P("cutoff", today.AddDays(-183).ToDateTime(TimeOnly.MinValue)));
        var clients = await Read<TopClientInsight>(db, """
            SELECT json_build_object('client',o.client,'orders',count(*),'revenuePln',coalesce(sum(qt.total_net),0))
            FROM public.orders o LEFT JOIN public.quotes qt ON qt.order_id=o.id WHERE o.status::text<>'rejected'
            GROUP BY o.client ORDER BY count(*) DESC,coalesce(sum(qt.total_net),0) DESC,min(o.id) LIMIT 5
            """, ct);
        const string overdue = " FROM public.orders o WHERE o.deadline<@today AND o.status::text NOT IN ('wydane','rejected')";
        var overdueTotal = (await Read<long>(db, "SELECT to_json(count(*))" + overdue, ct, P("today", today)))[0];
        var overdueRows = await Read<ScheduleInsight>(db, "SELECT " + ScheduleJson + overdue + " ORDER BY o.deadline,o.id LIMIT 100", ct, P("today", today));
        await tx.CommitAsync(ct);
        return new(counts.Total, counts.Rejected, InsightCalculations.Round(counts.Total == 0 ? 0 : (double)counts.Rejected / counts.Total * 100, 1), counts.Standard, counts.Custom,
            counts.Margin is null or 0 ? null : InsightCalculations.Round(counts.Margin.Value * 100, 1), counts.InProduction, counts.Done,
            Rounded(counts.Cycle), Rounded(counts.QuoteStart), Rounded(accuracy), revenue, clients, overdueRows, checked((int)overdueTotal));
    }
    private static double? Rounded(double? value) => value is null ? null : InsightCalculations.Round(value.Value, 1);
    private sealed record ProductionRow(long Id, string? OrderNumber, string Client, string Status, DateOnly? Deadline, string? Description, string? Material, JsonElement Processes, double? TotalNet);
    private sealed record ProfitRow(long Id, string OrderNumber, string Client, string Status, double Price, double Weight, double WeightRate, double Material, double Planned, double Actual);
    private sealed record Counts(int Total, int Rejected, int Standard, int Custom, int InProduction, int Done, double? Margin, double? Cycle, double? QuoteStart);
}
