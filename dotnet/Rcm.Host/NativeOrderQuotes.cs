using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;
using Rcm.Orders;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    private static readonly JsonSerializerOptions QuoteJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UtcTimestampConverter() }
    };

    public async Task<OrderQuoteDto> Quote(long id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return await ReadQuote(db, id, ct);
    }

    public async Task<OrderQuotePreview> Preview(OrderQuoteInput input, CancellationToken ct)
    {
        ValidateQuote(input);
        await using var db = await Open(ct);
        var result = CalculateQuote(input, await ReadLaborRate(db, ct));
        return new(result.OpsTotal, result.MaterialTotal, result.ExtraLabor, result.WeightTotal, result.Base,
            result.Subtotal, result.TotalNet, result.PricingMethod, input.WeightBasis);
    }

    public async Task<OrderQuoteDto> SaveQuote(LegacyUser actor, long id, OrderQuoteInput input, CancellationToken ct)
    {
        RequireEdit(actor, true);
        ValidateQuote(input);
        await using var db = await Open(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var order = await LockedOrder(db, id, ct);
        EnsureQuoteEditable(order);
        if (order.IsInternal && input.Method == "od_masy")
            throw new CrmFault(400, "Zlecenie wewnętrzne nie używa wyceny od masy");
        var result = CalculateQuote(input, await ReadLaborRate(db, ct));
        var processes = input.Processes.Select(p =>
        {
            var row = JsonSerializer.SerializeToNode(p, QuoteJson)!.AsObject();
            row["wydział"] = string.IsNullOrEmpty(p.Department) ? p.Name : p.Department;
            row["op"] = p.Name;
            row["cost"] = PricingCalculator.ProcessTotal(QuoteProcess(p));
            return row;
        }).ToArray();
        await Execute(db, """
            INSERT INTO public.quotes AS q
                (order_id, line_items, processes_json, materials_json, pricing_method, weight_basis,
                 material_cost, material_weight_kg, material_price_per_kg, weight_netto_kg, weight_brutto_kg,
                 weight_kg, weight_rate_pln_kg, labor_hours, overhead_pct, margin_pct, transport_cost,
                 show_unit_prices, total_net, is_zapor, welding_hours, estimate_version, created_at, last_edited_at)
            VALUES (@id, '[]'::json, @processes::json, @materials::json, @method, @basis,
                    @material_cost, @material_weight, @material_price, @netto, @brutto, @weight, @weight_rate,
                    @labor, @overhead, @margin, @transport, @unit_prices, @total, false, 0, 'v3',
                    CURRENT_TIMESTAMP AT TIME ZONE 'UTC', CURRENT_TIMESTAMP AT TIME ZONE 'UTC')
            ON CONFLICT (order_id) DO UPDATE SET
                line_items=EXCLUDED.line_items, processes_json=EXCLUDED.processes_json, materials_json=EXCLUDED.materials_json,
                pricing_method=EXCLUDED.pricing_method, weight_basis=EXCLUDED.weight_basis,
                material_cost=EXCLUDED.material_cost, material_weight_kg=EXCLUDED.material_weight_kg,
                material_price_per_kg=EXCLUDED.material_price_per_kg, weight_netto_kg=EXCLUDED.weight_netto_kg,
                weight_brutto_kg=EXCLUDED.weight_brutto_kg, weight_kg=EXCLUDED.weight_kg,
                weight_rate_pln_kg=EXCLUDED.weight_rate_pln_kg, labor_hours=EXCLUDED.labor_hours,
                overhead_pct=EXCLUDED.overhead_pct, margin_pct=EXCLUDED.margin_pct, transport_cost=EXCLUDED.transport_cost,
                show_unit_prices=EXCLUDED.show_unit_prices, total_net=EXCLUDED.total_net, is_zapor=false,
                estimate_version='v3', last_edited_at=EXCLUDED.last_edited_at
            """, ct, P("id", id), P("processes", JsonSerializer.Serialize(processes, QuoteJson)),
            P("materials", JsonSerializer.Serialize(input.Materials, QuoteJson)), P("method", input.Method), P("basis", input.WeightBasis),
            P("material_cost", result.MaterialTotal), P("material_weight", input.MaterialWeightKg), P("material_price", input.MaterialPricePerKg),
            P("netto", input.WeightNettoKg), P("brutto", input.WeightBruttoKg), P("weight", input.WeightKg), P("weight_rate", input.WeightRatePlnKg),
            P("labor", input.LaborHours), P("overhead", input.OverheadPct), P("margin", input.MarginPct), P("transport", input.TransportCost),
            P("unit_prices", input.ShowUnitPrices), P("total", result.TotalNet));
        await MarkQuoted(db, actor, order, ct);
        await Event(db, actor, id, "quote_saved", null, null,
            result.TotalNet.ToString("0.0###############", CultureInfo.InvariantCulture) + " zł · wycena strukturalna", ct);
        var saved = await ReadQuote(db, id, ct);
        await transaction.CommitAsync(ct);
        return saved;
    }

    public async Task<OrderQuoteDto> SaveManualQuote(LegacyUser actor, long id, OrderManualQuote input, CancellationToken ct)
    {
        RequireEdit(actor, true);
        if (!double.IsFinite(input.TotalNet) || input.TotalNet is < 0 or > 99_999_999.99)
            throw CrmFault.Invalid("totalNet", "Podaj nieujemną kwotę do 99 999 999,99 zł.");
        var total = PricingCalculator.Calculate(new StructuredQuoteInput
            { MaterialCost = input.TotalNet, OverheadPct = 0, MarginPct = 0 }).TotalNet;
        await using var db = await Open(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var order = await LockedOrder(db, id, ct);
        EnsureQuoteEditable(order);
        await Execute(db, """
            INSERT INTO public.quotes
                (order_id, total_net, line_items, processes_json, materials_json, labor_hours, material_cost,
                 material_weight_kg, material_price_per_kg, weight_kg, weight_rate_pln_kg, weight_netto_kg,
                 weight_brutto_kg, welding_hours, transport_cost, overhead_pct, margin_pct, pricing_method,
                 weight_basis, estimate_version, is_zapor, show_unit_prices, created_at, last_edited_at)
            VALUES (@id, @total, '[]'::json, '[]'::json, '[]'::json, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    'reczna', 'netto', 'manual', false, true,
                    CURRENT_TIMESTAMP AT TIME ZONE 'UTC', CURRENT_TIMESTAMP AT TIME ZONE 'UTC')
            ON CONFLICT (order_id) DO UPDATE SET
                total_net=EXCLUDED.total_net, line_items=EXCLUDED.line_items, processes_json=EXCLUDED.processes_json,
                materials_json=EXCLUDED.materials_json, labor_hours=0, material_cost=0, material_weight_kg=0,
                material_price_per_kg=0, weight_kg=0, weight_rate_pln_kg=0, weight_netto_kg=0, weight_brutto_kg=0,
                welding_hours=0, transport_cost=0, overhead_pct=0, margin_pct=0, pricing_method='reczna',
                weight_basis='netto', estimate_version='manual', last_edited_at=EXCLUDED.last_edited_at
            """, ct, P("id", id), P("total", total));
        await MarkQuoted(db, actor, order, ct);
        await Event(db, actor, id, "quote_saved", null, null,
            total.ToString("0.0###############", CultureInfo.InvariantCulture) + " zł · wycena ręczna", ct);
        var saved = await ReadQuote(db, id, ct);
        await transaction.CommitAsync(ct);
        return saved;
    }

    private static void EnsureQuoteEditable(OrderDto order)
    {
        if (order.Status is not ("niestandard" or "standard" or "quoted" or "in_production"))
            throw new CrmFault(409, "Nie można edytować wyceny dla tego statusu");
        if (order.Status == "in_production" && !order.IsInternal)
            throw new CrmFault(409, "Zmiana ceny zewnętrznego zlecenia wymaga ponownej akceptacji przed produkcją");
    }

    private async Task MarkQuoted(NpgsqlConnection db, LegacyUser actor, OrderDto order, CancellationToken ct)
    {
        if (order.Status is not ("standard" or "niestandard")) return;
        var status = order.IsInternal ? "in_production" : "quoted";
        await Execute(db, """
            UPDATE public.orders SET status=@status::public.orderstatus, version_id=version_id+1,
                quoted_at=COALESCE(quoted_at, CURRENT_TIMESTAMP AT TIME ZONE 'UTC'),
                started_at=CASE WHEN @internal THEN COALESCE(started_at, CURRENT_TIMESTAMP AT TIME ZONE 'UTC') ELSE started_at END
            WHERE id=@id
            """, ct, P("status", status), P("internal", order.IsInternal), P("id", order.Id));
        await Event(db, actor, order.Id, "quoted", order.Status, status, null, ct);
    }

    private async Task<OrderQuoteDto> ReadQuote(NpgsqlConnection db, long id, CancellationToken ct)
    {
        var payload = (await Read<JsonObject>(db, "SELECT to_jsonb(q) FROM public.quotes q WHERE order_id=@id", ct, P("id", id))).SingleOrDefault()
            ?? throw new CrmFault(404, "Brak wyceny dla tego zlecenia");
        static void Alias(JsonObject row, string field, params string[] aliases)
        {
            if (row[field] is JsonValue value && value.TryGetValue<string>(out var current) && !string.IsNullOrEmpty(current)) return;
            foreach (var alias in aliases)
                if (row[alias] is JsonValue candidate && candidate.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                { row[field] = text; return; }
        }
        if (payload["processes_json"] is JsonArray processes)
            foreach (var row in processes.OfType<JsonObject>())
            { Alias(row, "name", "op"); Alias(row, "department", "wydział"); Alias(row, "material", "materiał"); }
        if (payload["materials_json"] is JsonArray materials)
            foreach (var row in materials.OfType<JsonObject>()) Alias(row, "name", "material", "mat", "materiał");
        if (payload["pricing_method"] is null)
            payload["pricing_method"] = payload["estimate_version"]?.GetValue<string>() == "manual" ? "reczna" : "kalkulacja";
        OrderQuoteDto quote;
        try { quote = payload.Deserialize<OrderQuoteDto>(QuoteJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new CrmFault(503, "Nie można odczytać zapisanej wyceny. Zachowaj formularz."); }
        // Older saves expanded absent hours/rate to zero while retaining computed fixed cost.
        return quote with { ProcessesJson = quote.ProcessesJson?.Select(p =>
            p.Hours == 0 && p.RatePerHour == 0 && p.Cost > 0 ? p with { Hours = null, RatePerHour = null } : p).ToArray() };
    }

    private static ProcessLine QuoteProcess(OrderQuoteProcess p)
    {
        var line = new ProcessLine { Cost = p.Cost };
        if (p.Hours is { } hours) line = line with { Hours = hours };
        if (p.RatePerHour is { } rate) line = line with { RatePerHour = rate };
        return line;
    }

    private static StructuredQuoteResult CalculateQuote(OrderQuoteInput input, double laborRate) => PricingCalculator.Calculate(new StructuredQuoteInput
    {
        Processes = input.Processes.Select(QuoteProcess).ToArray(),
        Materials = input.Materials.Select(m => new MaterialLine { QtyKg = m.QtyKg, PricePerKg = m.PricePerKg, Cost = m.Cost }).ToArray(),
        MaterialCost = input.MaterialCost, MaterialWeightKg = input.MaterialWeightKg, MaterialPricePerKg = input.MaterialPricePerKg,
        LaborHours = input.LaborHours, LaborRate = laborRate, OverheadPct = input.OverheadPct, MarginPct = input.MarginPct,
        TransportCost = input.TransportCost, WeightKg = input.WeightKg, WeightRatePlnKg = input.WeightRatePlnKg, Method = input.Method
    });

    private static void ValidateQuote(OrderQuoteInput input)
    {
        var errors = new Dictionary<string, string[]>();
        void Number(string name, double value, double max)
        { if (!double.IsFinite(value) || value < 0 || value > max) errors[name] = ["Podaj nieujemną liczbę w dozwolonym zakresie."]; }
        const double money = 99_999_999.99, weight = 9_999_999.999, rate = 1_000_000;
        if (input.Method is not ("kalkulacja" or "od_masy")) errors["method"] = ["Wybierz metodę wyceny."];
        if (input.WeightBasis is not ("netto" or "brutto")) errors["weightBasis"] = ["Wybierz masę netto lub brutto."];
        Number("materialWeightKg", input.MaterialWeightKg, weight); Number("materialPricePerKg", input.MaterialPricePerKg, rate);
        Number("materialCost", input.MaterialCost, money); Number("weightNettoKg", input.WeightNettoKg, weight);
        Number("weightBruttoKg", input.WeightBruttoKg, weight); Number("laborHours", input.LaborHours, rate);
        Number("overheadPct", input.OverheadPct, 1); Number("marginPct", input.MarginPct, 1);
        Number("transportCost", input.TransportCost, money); Number("weightKg", input.WeightKg, weight);
        Number("weightRatePlnKg", input.WeightRatePlnKg, rate);
        if (input.Processes is null) errors["processes"] = ["Podaj listę operacji."];
        else for (var i = 0; i < input.Processes.Length; i++)
        {
            var p = input.Processes[i];
            if (p is null || p.Name is null) { errors[$"processes.{i}"] = ["Podaj nazwę operacji."]; continue; }
            Number($"processes.{i}.hours", p.Hours ?? 0, rate); Number($"processes.{i}.ratePerHour", p.RatePerHour ?? 0, rate); Number($"processes.{i}.cost", p.Cost, money);
        }
        if (input.Materials is null) errors["materials"] = ["Podaj listę materiałów."];
        else for (var i = 0; i < input.Materials.Length; i++)
        {
            var m = input.Materials[i];
            if (m is null) { errors[$"materials.{i}"] = ["Podaj materiał."]; continue; }
            Number($"materials.{i}.qtyKg", m.QtyKg, weight); Number($"materials.{i}.pricePerKg", m.PricePerKg, rate); Number($"materials.{i}.cost", m.Cost, money);
        }
        if (errors.Count > 0) throw new CrmFault(422, "Sprawdź dane wyceny.", errors);
    }
}
