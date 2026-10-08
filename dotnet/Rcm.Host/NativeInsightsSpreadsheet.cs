using Npgsql;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeInsights
{
    private static readonly SemaphoreSlim SpreadsheetSlots = new(2);
    public async Task<IResult> Export(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); var entered = false;
        try
        {
            if (!await SpreadsheetSlots.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)) throw new CrmFault(503, "Trwa generowanie innych eksportów. Ponów pobieranie.");
            entered = true;
            await using var db = await Open(timeout.Token); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, timeout.Token);
            var count = (await Read<long>(db, "SELECT to_json(count(*)) FROM public.orders", timeout.Token))[0];
            if (count > 100000) throw new CrmFault(422, "Eksport przekracza limit 100000 zleceń.");
            var bytes = await InsightSpreadsheet.Build(async xml =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT json_build_object('number',o.order_number,'client',o.client,'status',o.status,'branch',o.triage_branch,
                      'deadline',o.deadline::text,'value',coalesce(q.total_net,0),'created',to_char(o.created_at,'YYYY-MM-DD'))
                    FROM public.orders o LEFT JOIN public.quotes q ON q.order_id=o.id ORDER BY o.created_at DESC,o.id DESC
                    """, db);
                await using var reader = await command.ExecuteReaderAsync(timeout.Token); var index = 2;
                while (await reader.ReadAsync(timeout.Token))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var row = System.Text.Json.JsonSerializer.Deserialize<InsightSpreadsheet.Row>(reader.GetString(0), Json)!;
                    InsightSpreadsheet.WriteRow(xml, index++, row);
                }
            }, timeout.Token);
            await tx.CommitAsync(timeout.Token); return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "zlecenia_rcm.xlsx");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Eksport trwał zbyt długo. Ponów pobieranie."); }
        finally { if (entered) SpreadsheetSlots.Release(); }
    }
}
