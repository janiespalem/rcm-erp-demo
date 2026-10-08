using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    public async Task<OrderDto> Transition(LegacyUser actor, long id, string action, CancellationToken ct)
    {
        RequireEdit(actor, action is "complete" or "archive" or "restore");
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var order = action is "archive" or "restore" ? await GetOrder(db, id, ct, true) : await LockedOrder(db, id, ct);
        var next = order.Status;
        string change, eventType;
        switch (action)
        {
            case "archive":
                if (order.Status is not ("wydane" or "rejected")) throw new CrmFault(409, "Archiwizować można tylko zakończone lub odrzucone zlecenie.");
                if (order.ArchivedAt is not null) return order;
                change = "archived_at=timezone('UTC',now())"; eventType = "archived";
                break;
            case "restore":
                if (order.ArchivedAt is null) return order;
                change = "archived_at=NULL"; eventType = "restored";
                break;
            case "confirm":
                if (order.Status != "quoted") throw new CrmFault(409, "Tylko wycenione zlecenie można zatwierdzić.");
                await Execute(db, """
                    INSERT INTO public.price_history(order_type,total_price_historical,parameters_json,order_date,client)
                    SELECT coalesce(nullif(o.sop_name,''),o.order_type),q.total_net,
                      json_build_object('weight_kg',coalesce(nullif(q.material_weight_kg,0),q.weight_kg,0),
                        'pln_kg',q.total_net/coalesce(nullif(q.material_weight_kg,0),q.weight_kg,0),'material',o.material),@today,o.client
                    FROM public.orders o JOIN public.quotes q ON q.order_id=o.id
                    WHERE o.id=@id AND q.total_net<>0 AND coalesce(nullif(q.material_weight_kg,0),q.weight_kg,0)>0
                    """, ct, P("id", id), P("today", Today));
                next = "in_production"; eventType = "confirmed";
                change = "status='in_production',quoted_at=coalesce(quoted_at,timezone('UTC',now())),started_at=coalesce(started_at,timezone('UTC',now()))";
                break;
            case "complete":
                if (order.Status != "in_production") throw new CrmFault(409, "Tylko zlecenie w produkcji można oznaczyć jako gotowe.");
                await MaterialConsumption(db, id, ct);
                next = "gotowe"; eventType = "completed";
                change = "status='gotowe',completed_at=timezone('UTC',now())";
                break;
            case "deliver":
                if (order.Status is not ("gotowe" or "in_production")) throw new CrmFault(409, "Tylko zlecenie gotowe lub w produkcji można zakończyć.");
                if (order.Status == "in_production")
                {
                    RequireEdit(actor, true);
                    await MaterialConsumption(db, id, ct);
                }
                next = "wydane"; eventType = "delivered";
                change = "status='wydane',delivered_at=timezone('UTC',now())" + (order.Status == "in_production" ? ",completed_at=coalesce(completed_at,timezone('UTC',now()))" : "");
                break;
            default: throw new CrmFault(404, "Nieznana operacja zlecenia.");
        }
        var result = (await Read<OrderDto>(db, $"UPDATE public.orders SET {change},version_id=version_id+1 WHERE id=@id RETURNING row_to_json(orders)", ct, P("id", id)))[0];
        await Event(db, actor, id, eventType, action is "archive" or "restore" ? null : order.Status,
            action is "archive" or "restore" ? null : next, null, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static Task MaterialConsumption(NpgsqlConnection db, long id, CancellationToken ct) => Execute(db, """
        INSERT INTO public.stock_movements(doc_type,order_id,item_name,qty,unit,material_id,moved_at)
        SELECT 'rozchod',o.id,'Automatyczny rozchod materialow dla zlecenia: ' || coalesce(o.order_number,'None'),
          coalesce(nullif(q.material_weight_kg,0),q.weight_kg,0),'kg',o.approved_material_id,timezone('UTC',now())
        FROM public.orders o JOIN public.quotes q ON q.order_id=o.id
        WHERE o.id=@id AND o.approved_material_id IS NOT NULL AND coalesce(nullif(q.material_weight_kg,0),q.weight_kg,0)>0
          AND NOT EXISTS(SELECT 1 FROM public.stock_movements m WHERE m.order_id=o.id AND m.material_id=o.approved_material_id AND m.doc_type='rozchod')
        """, ct, P("id", id));
}
