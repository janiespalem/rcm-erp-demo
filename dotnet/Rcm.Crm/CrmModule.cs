using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rcm.Contracts;

namespace Rcm.Crm;

public sealed record CrmActor(long UserId, string Name, Guid TeamId);
public sealed class CrmFault(int status, string message, Dictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public Dictionary<string, string[]>? Errors { get; } = errors;
    public static CrmFault Invalid(string field, string message) => new(422, "Sprawdź formularz.", new() { [field] = [message] });
    public static CrmFault Conflict() => new(409, "Dane zostały zmienione. Porównaj zapisane dane ze swoim formularzem.");
}

public sealed class CrmModule(CrmDb db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => clock.GetUtcNow();
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime);
    public static CustomerDto Dto(Customer c) => new(c.Id, c.Version, new(c.DisplayName, c.ContactPerson, c.Phone, c.Email, c.Source, c.OriginalNote), c.IsSynthetic, c.CreatedAt, c.ArchivedAt, c.ArchivedBy);
    public static TopicDto Dto(Topic t) => new(t.Id, t.CustomerId, t.Version, new(t.Products, t.Need, t.State, Plan(t.NextDate, t.NextDescription, t.NextAction)));
    private static TopicSummary Summary(Topic t) => new(t.Id, t.Products, t.State, Plan(t.NextDate, t.NextDescription, t.NextAction));
    private static ContactPlan? Plan(DateOnly? date, string? description, string? action) => date is null && description is null ? null : new(date, description, action);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Phone(string? s) => Clean(new string((s ?? "").Where(char.IsAsciiDigit).ToArray()));
    private static void Limit(string? value, int max, string field)
    {
        if (value?.Length > max) throw CrmFault.Invalid(field, $"Maksymalnie {max} znaków.");
    }
    private static void Check(CustomerFields? f)
    {
        if (f is null || string.IsNullOrWhiteSpace(f.DisplayName)) throw CrmFault.Invalid("displayName", "Wpisz nazwę klienta.");
        Limit(f.DisplayName, 240, "displayName"); Limit(f.ContactPerson, 240, "contactPerson");
        Limit(f.Phone, 80, "phone"); Limit(f.Email, 320, "email"); Limit(f.Source, 160, "source"); Limit(f.OriginalNote, 8000, "originalNote");
        if (Clean(f.Email) is { } email && (!MailAddress.TryCreate(email, out var address) || address.Address != email))
            throw CrmFault.Invalid("email", "Sprawdź adres e-mail.");
    }
    private static void Check(TopicFields? f)
    {
        if (f?.Products is null || f.Products.Length is < 1 or > 5 || f.Products.Any(p => !CrmVocabulary.Products.Contains(p)))
            throw CrmFault.Invalid("products", "Wybierz przynajmniej jeden produkt z listy.");
        Limit(f.Need, 8000, "need"); CheckState(f.State, f.NextContact);
    }
    private static void CheckState(string? state, ContactPlan? p)
    {
        if (state is null || !CrmVocabulary.States.ContainsKey(state)) throw CrmFault.Invalid("state", "Wybierz stan rozmów.");
        if (p is null || state == "closed") return;
        if ((p.Date is not null) == (Clean(p.Description) is not null)) throw CrmFault.Invalid("nextContact", "Podaj datę albo termin opisowy.");
        Limit(p.Description, 1000, "nextContact"); Limit(p.Action, 1000, "nextContact");
    }
    private static void Apply(Customer c, CustomerFields f)
    {
        c.DisplayName = f.DisplayName.Trim(); c.ContactPerson = Clean(f.ContactPerson); c.Phone = Clean(f.Phone);
        c.Email = Clean(f.Email); c.Source = Clean(f.Source); c.NormalizedPhone = Phone(f.Phone); c.NormalizedEmail = Clean(f.Email)?.ToLowerInvariant();
        c.SearchText = string.Join(' ', c.DisplayName, c.ContactPerson, c.Phone, c.Email, c.NormalizedPhone).ToLowerInvariant();
    }
    private static void Apply(Topic t, string state, ContactPlan? p)
    {
        t.State = state;
        if (state == "closed") p = null;
        t.NextDate = p?.Date; t.NextDescription = Clean(p?.Description); t.NextAction = Clean(p?.Action);
    }
    private static void Version(long actual, long expected)
    {
        if (expected < 1) throw CrmFault.Invalid("expectedVersion", "Brak wersji zapisu.");
        if (actual != expected) throw CrmFault.Conflict();
    }
    private async Task<T> Command<T>(CrmActor actor, Guid requestId, string operation, object payload, Func<Task<T>> action, CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Brak identyfikatora zapisu.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + JsonSerializer.Serialize(payload, Json))));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            var lockKey = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{actor.TeamId}/{actor.UserId}/{requestId}")));
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
            var prior = await db.Receipts.FindAsync([actor.TeamId, actor.UserId, requestId], ct);
            if (prior is not null)
            {
                if (prior.Fingerprint != hash) throw new CrmFault(409, "Identyfikator zapisu został już użyty dla innych danych.");
                return JsonSerializer.Deserialize<T>(prior.Result, Json)!;
            }
            var result = await action();
            db.Receipts.Add(new() { TeamId = actor.TeamId, ActorId = actor.UserId, RequestId = requestId,
                Fingerprint = hash, Result = JsonSerializer.Serialize(result, Json), RecordedAt = Now });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException) { throw CrmFault.Conflict(); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new CrmFault(503, "Zapis czekał zbyt długo. Zachowaj formularz i ponów ten sam zapis.");
        }
        catch (InvalidOperationException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable })
        {
            throw new CrmFault(503, "Zapis czekał zbyt długo. Zachowaj formularz i ponów ten sam zapis.");
        }
    }
    private async Task<Customer> FindCustomer(CrmActor a, Guid id, CancellationToken ct) =>
        await db.Customers.SingleOrDefaultAsync(c => c.TeamId == a.TeamId && c.Id == id, ct) ?? throw new CrmFault(404, "Nie znaleziono klienta.");
    private async Task<Customer> LockCustomer(CrmActor a, Guid id, CancellationToken ct) =>
        await db.Customers.FromSqlInterpolated($"SELECT * FROM crm.\"Customers\" WHERE \"TeamId\" = {a.TeamId} AND \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new CrmFault(404, "Nie znaleziono klienta.");
    private static void Active(Customer c)
    {
        if (c.ArchivedAt is not null) throw new CrmFault(410, "Klient został usunięty. Przywróć go przed zapisaniem zmian.");
    }
    private async Task<Customer> LockTopicCustomer(CrmActor a, Guid topicId, CancellationToken ct)
    {
        var customerId = await db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && t.Id == topicId)
            .Select(t => (Guid?)t.CustomerId).SingleOrDefaultAsync(ct) ?? throw new CrmFault(404, "Nie znaleziono tematu.");
        return await LockCustomer(a, customerId, ct);
    }
    private async Task<Topic> FindTopic(CrmActor a, Guid id, CancellationToken ct) =>
        await db.Topics.SingleOrDefaultAsync(t => t.TeamId == a.TeamId && t.Id == id, ct) ?? throw new CrmFault(404, "Nie znaleziono tematu.");
    private async Task<CustomerDto[]> Duplicates(CrmActor a, Customer c, CancellationToken ct) =>
        (await db.Customers.AsNoTracking().Where(x => x.TeamId == a.TeamId && x.Id != c.Id &&
            ((c.NormalizedPhone != null && x.NormalizedPhone == c.NormalizedPhone) || (c.NormalizedEmail != null && x.NormalizedEmail == c.NormalizedEmail)))
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Take(10).ToArrayAsync(ct)).Select(Dto).ToArray();
    public Task<SaveCustomerResult> Create(CrmActor a, CreateCustomer cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, "customer:create", cmd, async () =>
        {
            Check(cmd.Fields);
            var c = new Customer { Id = Guid.NewGuid(), TeamId = a.TeamId, CreatedAt = Now, IsSynthetic = cmd.IsSynthetic, OriginalNote = Clean(cmd.Fields.OriginalNote) };
            Apply(c, cmd.Fields); db.Customers.Add(c);
            db.CustomerChanges.Add(new() { Id = Guid.NewGuid(), TeamId = a.TeamId, CustomerId = c.Id, ActorId = a.UserId, RecordedAt = Now, Changes = "{\"created\":true}" });
            return new SaveCustomerResult(Dto(c), await Duplicates(a, c, ct));
        }, ct);
    public Task<SaveCustomerResult> Edit(CrmActor a, Guid id, EditCustomer cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"customer:{id}:edit", cmd, async () =>
        {
            Check(cmd.Fields); var c = await LockCustomer(a, id, ct); Active(c); Version(c.Version, cmd.ExpectedVersion);
            if (Clean(cmd.Fields.OriginalNote) != c.OriginalNote) throw CrmFault.Invalid("originalNote", "Pierwotna notatka pozostaje zachowana. Dodaj nową informację w historii tematu.");
            var before = Dto(c).Fields; Apply(c, cmd.Fields); c.Version++;
            db.CustomerChanges.Add(new() { Id = Guid.NewGuid(), TeamId = a.TeamId, CustomerId = c.Id, ActorId = a.UserId, RecordedAt = Now, Changes = Changes(before, Dto(c).Fields) });
            return new SaveCustomerResult(Dto(c), await Duplicates(a, c, ct));
        }, ct);
    public Task<CustomerDto> Archive(CrmActor a, Guid id, ChangeCustomerLifecycle cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"customer:{id}:archive", cmd, async () =>
        {
            var c = await LockCustomer(a, id, ct); Active(c); Version(c.Version, cmd.ExpectedVersion);
            c.ArchivedAt = Now; c.ArchivedBy = a.UserId; c.Version++;
            db.CustomerChanges.Add(new() { Id = Guid.NewGuid(), TeamId = a.TeamId, CustomerId = id,
                ActorId = a.UserId, RecordedAt = Now, Changes = JsonSerializer.Serialize(new { archived = true }, Json) });
            return Dto(c);
        }, ct);
    public Task<CustomerDto> Restore(CrmActor a, Guid id, ChangeCustomerLifecycle cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"customer:{id}:restore", cmd, async () =>
        {
            var c = await LockCustomer(a, id, ct);
            if (c.ArchivedAt is null) throw new CrmFault(409, "Klient jest już aktywny.");
            Version(c.Version, cmd.ExpectedVersion);
            c.ArchivedAt = null; c.ArchivedBy = null; c.Version++;
            db.CustomerChanges.Add(new() { Id = Guid.NewGuid(), TeamId = a.TeamId, CustomerId = id,
                ActorId = a.UserId, RecordedAt = Now, Changes = JsonSerializer.Serialize(new { restored = true }, Json) });
            return Dto(c);
        }, ct);
    public Task<TopicDto> CreateTopic(CrmActor a, Guid customerId, CreateTopic cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"customer:{customerId}:topic", cmd, async () =>
        {
            Check(cmd.Fields); Active(await LockCustomer(a, customerId, ct));
            var t = new Topic { Id = Guid.NewGuid(), TeamId = a.TeamId, CustomerId = customerId, Products = cmd.Fields.Products.Distinct().ToArray(), Need = Clean(cmd.Fields.Need) };
            Apply(t, cmd.Fields.State, cmd.Fields.NextContact); db.Topics.Add(t); Event(a, t, "created", t.Need, null);
            return Dto(t);
        }, ct);
    public Task<TopicDto> EditTopic(CrmActor a, Guid id, EditTopic cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"topic:{id}:edit", cmd, async () =>
        {
            Check(cmd.Fields); Active(await LockTopicCustomer(a, id, ct)); var t = await FindTopic(a, id, ct); Version(t.Version, cmd.ExpectedVersion);
            var before = Dto(t).Fields; t.Products = cmd.Fields.Products.Distinct().ToArray(); t.Need = Clean(cmd.Fields.Need);
            Apply(t, cmd.Fields.State, cmd.Fields.NextContact); t.Version++; Event(a, t, "changed", "Zmieniono temat / plan kontaktu.", Changes(before, Dto(t).Fields));
            return Dto(t);
        }, ct);
    public Task<TopicDto> Record(CrmActor a, Guid id, RecordContact cmd, CancellationToken ct) =>
        Command(a, cmd.RequestId, $"topic:{id}:contact", cmd, async () =>
        {
            CheckState(cmd.State, cmd.NextContact); Limit(cmd.Note, 8000, "note");
            Active(await LockTopicCustomer(a, id, ct)); var t = await FindTopic(a, id, ct); Version(t.Version, cmd.ExpectedVersion);
            var before = Dto(t).Fields; Apply(t, cmd.State, cmd.NextContact); t.Version++;
            Event(a, t, "conversation", Clean(cmd.Note), Changes(before, Dto(t).Fields)); return Dto(t);
        }, ct);
    private void Event(CrmActor a, Topic t, string kind, string? note, string? changes) => db.Events.Add(new()
    {
        Id = Guid.NewGuid(), TeamId = a.TeamId, TopicId = t.Id, ActorId = a.UserId, ActorName = a.Name, RecordedAt = Now,
        Kind = kind, Note = note, State = t.State, NextDate = t.NextDate, NextDescription = t.NextDescription, NextAction = t.NextAction, Changes = changes
    });
    private static string Changes<T>(T before, T after)
    {
        var old = JsonSerializer.SerializeToElement(before, Json); var updated = JsonSerializer.SerializeToElement(after, Json);
        return JsonSerializer.Serialize(updated.EnumerateObject().Where(p => p.Value.GetRawText() != old.GetProperty(p.Name).GetRawText())
            .ToDictionary(p => p.Name, p => new { before = old.GetProperty(p.Name), after = p.Value }), Json);
    }
    public async Task<CustomerDetail> Detail(CrmActor a, Guid id, CancellationToken ct)
    {
        var c = await FindCustomer(a, id, ct);
        var topics = await db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && t.CustomerId == id).OrderBy(t => t.Id).ToArrayAsync(ct);
        return new(Dto(c), topics.Select(Dto).ToArray());
    }
    public async Task<CustomerDto> CustomerRecord(CrmActor a, Guid id, CancellationToken ct) => Dto(await FindCustomer(a, id, ct));
    public async Task<TopicDto> TopicRecord(CrmActor a, Guid id, CancellationToken ct) => Dto(await FindTopic(a, id, ct));
    public async Task<Page<TopicDto>> CustomerTopics(CrmActor a, Guid id, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size); await FindCustomer(a, id, ct);
        var q = db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && t.CustomerId == id);
        var total = await q.CountAsync(ct);
        var topics = await q.OrderBy(t => t.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        return new(topics.Select(Dto).ToArray(), total, page, size);
    }
    public async Task<Page<CustomerChangeDto>> CustomerHistory(CrmActor a, Guid id, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size); await FindCustomer(a, id, ct);
        var q = db.CustomerChanges.AsNoTracking().Where(x => x.TeamId == a.TeamId && x.CustomerId == id);
        var total = await q.CountAsync(ct);
        var changes = await q.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        return new(changes.Select(x => new CustomerChangeDto(x.Id, x.ActorId, x.RecordedAt, x.Changes)).ToArray(), total, page, size);
    }
    private static int Offset(int page, int size)
    {
        if (page < 1 || page > 100000 || size is < 1 or > 100) throw CrmFault.Invalid("page", "Rozmiar strony: 1–100, numer strony: 1–100000.");
        return (page - 1) * size;
    }
    private IQueryable<Topic> FilterDue(IQueryable<Topic> q, string? due)
    {
        var today = Today;
        return due switch
        {
            null or "" => q,
            "today" => q.Where(t => t.State != "closed" && t.NextDate == today),
            "overdue" => q.Where(t => t.State != "closed" && t.NextDate < today),
            "future" => q.Where(t => t.State != "closed" && t.NextDate > today),
            "unclear" => q.Where(t => t.State != "closed" && t.NextDate == null && t.NextDescription != null),
            "none" => q.Where(t => t.NextDate == null && t.NextDescription == null),
            _ => throw CrmFault.Invalid("due", "Nieznany filtr terminu.")
        };
    }
    private IQueryable<Customer> FilterCustomers(CrmActor a, string? search, string? product, string? state, string? due, bool archived)
    {
        Limit(search, 240, "q");
        var q = db.Customers.AsNoTracking().Where(c => c.TeamId == a.TeamId && (c.ArchivedAt != null) == archived);
        if (Clean(search) is { } term)
        {
            term = term.ToLowerInvariant();
            var escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var digits = Phone(term);
            q = q.Where(c => EF.Functions.Like(c.SearchText, "%" + escaped + "%", "\\") || (digits != null && c.NormalizedPhone == digits));
        }
        if (Clean(product) is not null || Clean(state) is not null || Clean(due) is not null)
        {
            var topics = FilterDue(db.Topics.Where(t => t.TeamId == a.TeamId), due);
            if (Clean(product) is not null) topics = topics.Where(t => t.Products.Contains(product!));
            if (Clean(state) is not null) topics = topics.Where(t => t.State == state);
            q = q.Where(c => topics.Any(t => t.CustomerId == c.Id) || (due == "none" && (product == null || product == "") && (state == null || state == "") && !c.Topics.Any()));
        }
        return q;
    }
    public async Task<Page<CustomerRow>> Query(CrmActor a, string? search, string? product, string? state, string? due, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size);
        var q = FilterCustomers(a, search, product, state, due, false);
        var total = await q.CountAsync(ct);
        var customers = await q.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        var ids = customers.Select(c => c.Id).ToArray();
        var selectedTopics = await db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && ids.Contains(t.CustomerId)).ToArrayAsync(ct);
        var last = await db.Events.Where(e => e.TeamId == a.TeamId && ids.Contains(e.Topic.CustomerId) && e.Kind == "conversation")
            .GroupBy(e => e.Topic.CustomerId).Select(g => new { Id = g.Key, Last = g.Max(e => e.RecordedAt) }).ToDictionaryAsync(x => x.Id, x => x.Last, ct);
        return new(customers.Select(c => new CustomerRow(Dto(c), selectedTopics.Where(t => t.CustomerId == c.Id).Select(Dto).ToArray(), last.TryGetValue(c.Id, out var at) ? at : null)).ToArray(), total, page, size);
    }
    public async Task<Page<CustomerSummaryRow>> Summaries(CrmActor a, string? search, string? product, string? state, string? due, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size);
        var q = FilterCustomers(a, search, product, state, due, false);
        var total = await q.CountAsync(ct);
        var customers = await q.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        var ids = customers.Select(c => c.Id).ToArray();
        var topics = await db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && ids.Contains(t.CustomerId))
            .GroupBy(t => t.CustomerId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var previews = await db.Topics.FromSqlInterpolated($"""
            SELECT * FROM (
                SELECT t.*, row_number() OVER (PARTITION BY t."CustomerId" ORDER BY t."Id") AS rn
                FROM crm."Topics" AS t WHERE t."TeamId" = {a.TeamId} AND t."CustomerId" = ANY ({ids})
            ) AS ranked WHERE rn <= 3
            """).AsNoTracking().ToArrayAsync(ct);
        var last = await db.Events.AsNoTracking().Where(e => e.TeamId == a.TeamId && ids.Contains(e.Topic.CustomerId) && e.Kind == "conversation")
            .GroupBy(e => e.Topic.CustomerId).Select(g => new { Id = g.Key, Last = g.Max(e => e.RecordedAt) }).ToDictionaryAsync(x => x.Id, x => x.Last, ct);
        return new(customers.Select(c => new CustomerSummaryRow(Dto(c), previews.Where(t => t.CustomerId == c.Id).Select(Summary).ToArray(),
            topics.GetValueOrDefault(c.Id), last.TryGetValue(c.Id, out var at) ? at : null)).ToArray(), total, page, size);
    }
    public async Task<Page<CustomerDto>> Archived(CrmActor a, string? search, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size);
        var q = FilterCustomers(a, search, null, null, null, true);
        var total = await q.CountAsync(ct);
        var customers = await q.OrderByDescending(c => c.ArchivedAt).ThenBy(c => c.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        return new(customers.Select(Dto).ToArray(), total, page, size);
    }
    public async Task<Page<QueueRow>> Queue(CrmActor a, string group, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size);
        if (group is not ("today" or "overdue" or "unclear")) throw CrmFault.Invalid("group", "Wybierz grupę kontaktów.");
        var q = FilterDue(db.Topics.AsNoTracking().Where(t => t.TeamId == a.TeamId && t.Customer.ArchivedAt == null), group);
        var total = await q.CountAsync(ct);
        var topics = await q.Include(t => t.Customer).OrderBy(t => t.NextDate).ThenBy(t => t.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        return new(topics.Select(t => new QueueRow(Dto(t.Customer), Dto(t), group)).ToArray(), total, page, size);
    }
    public async Task<Page<ContactEventDto>> History(CrmActor a, Guid topicId, int page, int size, CancellationToken ct)
    {
        var offset = Offset(page, size); await FindTopic(a, topicId, ct);
        var q = db.Events.AsNoTracking().Where(e => e.TeamId == a.TeamId && e.TopicId == topicId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.RecordedAt).ThenByDescending(e => e.Id).Skip(offset).Take(size).ToArrayAsync(ct);
        return new(items.Select(e => new ContactEventDto(e.Id, e.TopicId, e.ActorId, e.ActorName, e.RecordedAt, e.Kind, e.Note, e.State,
            Plan(e.NextDate, e.NextDescription, e.NextAction), e.Changes)).ToArray(), total, page, size);
    }
}
