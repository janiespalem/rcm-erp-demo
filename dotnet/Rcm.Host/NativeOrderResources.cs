using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders
{
    private const string OperationSelect = """
        SELECT json_build_object('id',op.id,'name',coalesce(c.name,'Op #' || op.id::text),
            'department',c.department,'responsible',op.responsible,'sequence',op.sequence,
            'status',op.status,'actual_hours',op.actual_hours)
        FROM public.order_operations op LEFT JOIN public.operation_catalog c ON c.id=op.catalog_id
        """;

    public async Task<OrderOperationRow[]> Operations(long id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await GetOrder(db, id, ct);
        return await Read<OrderOperationRow>(db, OperationSelect +
            " WHERE op.order_id=@id ORDER BY coalesce(op.sequence,0),op.id", ct, P("id", id));
    }

    public Task<OrderOperationRow> SetHours(LegacyUser actor, long orderId, long opId, SetOrderHours command, CancellationToken ct)
    {
        RequireEdit(actor, true);
        if (!double.IsFinite(command.ActualHours) || command.ActualHours is < 0 or > 999_999.99)
            throw CrmFault.Invalid("actualHours", "Podaj liczbę godzin od 0 do 999 999,99.");
        return ResourceCommand(actor, orderId, opId, command.RequestId, "actual_hours", command.ActualHours, async db =>
        {
            await ResourceOperation(db, orderId, opId, ct, true);
            await Execute(db, "UPDATE public.order_operations SET actual_hours=@hours WHERE order_id=@order AND id=@op",
                ct, P("hours", command.ActualHours), P("order", orderId), P("op", opId));
            await Event(db, actor, orderId, "actual_hours_updated", null, null,
                $"Operacja {opId}: {command.ActualHours.ToString(CultureInfo.InvariantCulture)} h", ct);
            return await ResourceOperation(db, orderId, opId, ct);
        }, ct);
    }

    public async Task<OrderQuestionDto[]> Questions(long id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await GetOrder(db, id, ct);
        return await Read<OrderQuestionDto>(db,
            "SELECT row_to_json(q) FROM public.parameter_requests q WHERE order_id=@id ORDER BY asked_at,id", ct, P("id", id));
    }

    public Task<OrderQuestionDto> AskQuestion(LegacyUser actor, long orderId, AskOrderQuestion command, CancellationToken ct)
    {
        RequireEdit(actor, true);
        var question = ResourceText(command.QuestionText, "questionText", "Wpisz pytanie (do 2000 znaków).");
        return ResourceCommand(actor, orderId, null, command.RequestId, "question_ask", question, async db =>
        {
            var result = (await Read<OrderQuestionDto>(db, """
                INSERT INTO public.parameter_requests(order_id,question_text,status,asked_at)
                VALUES(@order,@question,'pending',timezone('UTC',now()))
                RETURNING row_to_json(parameter_requests)
                """, ct, P("order", orderId), P("question", question)))[0];
            await Event(db, actor, orderId, "question_asked", null, null, question, ct);
            return result;
        }, ct);
    }

    public Task<OrderQuestionDto> AnswerQuestion(LegacyUser actor, long orderId, long questionId, AnswerOrderQuestion command, CancellationToken ct)
    {
        RequireEdit(actor);
        var answer = ResourceText(command.AnswerText, "answerText", "Wpisz odpowiedź (do 2000 znaków).");
        return ResourceCommand(actor, orderId, questionId, command.RequestId, "question_answer", answer, async db =>
        {
            var question = (await Read<OrderQuestionDto>(db, """
                SELECT row_to_json(q) FROM public.parameter_requests q
                WHERE order_id=@order AND id=@question FOR UPDATE
                """, ct, P("order", orderId), P("question", questionId))).SingleOrDefault()
                ?? throw new CrmFault(404, "Pytanie nie znalezione.");
            if (question.Status != "pending") throw new CrmFault(409, "Pytanie ma już odpowiedź.");
            var result = (await Read<OrderQuestionDto>(db, """
                UPDATE public.parameter_requests SET answer_text=@answer,status='answered',answered_at=timezone('UTC',now())
                WHERE order_id=@order AND id=@question RETURNING row_to_json(parameter_requests)
                """, ct, P("answer", answer), P("order", orderId), P("question", questionId)))[0];
            await Event(db, actor, orderId, "question_answered", null, null, answer, ct);
            return result;
        }, ct);
    }

    private static string ResourceText(string? value, string field, string message)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2000) throw CrmFault.Invalid(field, message);
        return text;
    }

    private static async Task<OrderOperationRow> ResourceOperation(NpgsqlConnection db, long orderId, long opId, CancellationToken ct, bool locked = false)
        => (await Read<OrderOperationRow>(db, OperationSelect + " WHERE op.order_id=@order AND op.id=@op" + (locked ? " FOR UPDATE OF op" : ""),
            ct, P("order", orderId), P("op", opId))).SingleOrDefault() ?? throw new CrmFault(404, "Operacja nie znaleziona.");

    private async Task<T> ResourceCommand<T>(LegacyUser actor, long orderId, long? targetId, Guid requestId,
        string commandType, object payload, Func<NpgsqlConnection, Task<T>> change, CancellationToken ct) where T : class
    {
        if (requestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));
        await using var db = await Open(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(requestId.ToString())));
        await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", key));
        var receipt = (await Read<ResourceReceipt>(db,
            "SELECT row_to_json(r) FROM public.order_command_receipts r WHERE request_id=@request", ct, P("request", requestId.ToString()))).SingleOrDefault();
        if (receipt is not null)
        {
            if (receipt.ActorId != actor.Id || receipt.OrderId != orderId || receipt.TargetId != targetId ||
                receipt.CommandType != commandType || receipt.PayloadHash != hash)
                throw new CrmFault(409, "Identyfikator zapisu należy do innej operacji lub użytkownika.");
            return receipt.ResponseJson.Deserialize<T>(Json) ?? throw new CrmFault(503, "Nie można odczytać potwierdzenia zapisu.");
        }
        await LockedOrder(db, orderId, ct);
        var result = await change(db);
        await Execute(db, """
            INSERT INTO public.order_command_receipts
                (request_id,actor_id,command_type,order_id,target_id,payload_hash,response_json,created_at)
            VALUES(@request,@actor,@type,@order,@target,@hash,@response::json,now())
            """, ct, P("request", requestId.ToString()), P("actor", actor.Id), P("type", commandType), P("order", orderId),
            P("target", targetId), P("hash", hash), P("response", JsonSerializer.Serialize(result, Json)));
        await transaction.CommitAsync(ct);
        return result;
    }

    private sealed record ResourceReceipt(long ActorId, string CommandType, long OrderId, long? TargetId,
        string PayloadHash, JsonElement ResponseJson);
}
