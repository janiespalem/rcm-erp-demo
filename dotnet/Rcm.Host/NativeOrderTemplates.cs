using System.Text.Json;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    public Task<OrderSavedTemplate> SaveAsTemplate(LegacyUser actor, long orderId, SaveOrderAsTemplate command, CancellationToken ct)
    {
        RequireEdit(actor, true);
        if (command.Name?.Length > 200) throw CrmFault.Invalid("name", "Maksymalnie 200 znaków.");
        if (command.Category?.Length > 50) throw CrmFault.Invalid("category", "Maksymalnie 50 znaków.");
        return ResourceCommand(actor, orderId, null, command.RequestId, "save_as_template", new { command.Name, command.Category }, async db =>
        {
            var order = await GetOrder(db, orderId, ct);
            var quote = (await Read<OrderQuoteDto>(db, "SELECT row_to_json(q) FROM public.quotes q WHERE order_id=@id ORDER BY id LIMIT 1", ct, P("id", orderId))).SingleOrDefault();
            var name = string.IsNullOrEmpty(command.Name) ? $"Z zlecenia {order.OrderNumber ?? "None"}" : command.Name;
            var category = string.IsNullOrEmpty(command.Category) ? string.IsNullOrEmpty(order.OrderType) ? "remont" : order.OrderType : command.Category;
            var operations = quote?.ProcessesJson?.Select(p => new { op = p.Name, hours = 0, cost = p.Cost }).ToArray();
            var weight = quote is null ? 0 : quote.MaterialWeightKg is > 0 ? quote.MaterialWeightKg.Value : quote.WeightKg ?? 0;
            var materials = weight > 0 ? JsonSerializer.Serialize(new[] { new { mat = string.IsNullOrEmpty(order.Material) ? "Materiał" : order.Material, qty_kg = weight, unit = "kg" } }) : "[]";
            var result = (await Read<OrderSavedTemplate>(db, """
                INSERT INTO public.product_templates(name,category,operations_json,materials_json,instruction_blocks,machines_json,base_price_pln,margin_pct,is_active)
                VALUES(@name,@category,@operations::json,@materials::json,'[]'::json,'[]'::json,@price,@margin,true)
                RETURNING json_build_object('id',id,'name',name,'category',category)
                """, ct, P("name", name), P("category", category), P("operations", operations is null ? "[]" : JsonSerializer.Serialize(operations)),
                P("materials", materials), P("price", quote?.TotalNet is > 0 ? quote.TotalNet : null),
                P("margin", quote?.MarginPct is > 0 ? quote.MarginPct : .25)))[0];
            return result;
        }, ct);
    }
}
