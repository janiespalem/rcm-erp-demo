using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    public async Task<Page<OrderQuestionQueueRow>> QuestionQueue(string? q, string? status, int page, int pageSize, CancellationToken ct)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100 || q?.Length > 200)
            throw CrmFault.Invalid("page", "Wybierz stronę i rozmiar od 1 do 100 pozycji; wyszukiwanie do 200 znaków.");
        if (!string.IsNullOrEmpty(status) && status is not ("pending" or "answered"))
            throw CrmFault.Invalid("status", "Wybierz prawidłowy stan pytania.");
        await using var db = await Open(ct);
        await using var transaction = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string from = """
            FROM public.parameter_requests r JOIN public.orders o ON o.id=r.order_id
            WHERE (@status='' OR r.status::text=@status) AND (@q='' OR strpos(lower(r.question_text),@q)>0
              OR strpos(lower(r.answer_text),@q)>0 OR strpos(lower(o.client),@q)>0 OR strpos(lower(o.order_number),@q)>0)
            """;
        NpgsqlParameter[] Arguments() => [P("status", status ?? ""), P("q", q?.Trim().ToLowerInvariant() ?? "")];
        var total = (await Read<long>(db, "SELECT to_json(count(*)) " + from, ct, Arguments()))[0];
        var rows = await Read<OrderQuestionQueueRow>(db, """
            SELECT json_build_object('id',r.id,'order_id',r.order_id,'order_number',o.order_number,'client',o.client,
              'deadline',o.deadline,'archived_at',o.archived_at,'question_text',r.question_text,'answer_text',r.answer_text,
              'status',r.status,'asked_at',r.asked_at,'answered_at',r.answered_at)
            """ + from + " ORDER BY r.asked_at DESC,r.id DESC LIMIT @limit OFFSET @offset", ct,
            [..Arguments(), P("limit", pageSize), P("offset", (page - 1) * pageSize)]);
        await transaction.CommitAsync(ct);
        return new(rows, checked((int)total), page, pageSize);
    }
}
