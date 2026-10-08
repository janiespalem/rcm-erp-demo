namespace Rcm.Contracts;

public static class CrmVocabulary
{
    public static readonly string[] Products = ["CT1", "CT MAX", "PK1", "Przyczepa specjalna", "Zbrojenie"];
    public static readonly IReadOnlyDictionary<string, string> States = new Dictionary<string, string>
    {
        ["new"] = "Nowe zapytanie", ["offer"] = "Oferta wysłana", ["finance"] = "Czeka na finansowanie",
        ["waiting"] = "Czeka na decyzję", ["noanswer"] = "Nie odpowiada", ["visit"] = "Chce obejrzeć produkt",
        ["later"] = "Możliwy powrót później", ["closed"] = "Kupiono u konkurencji"
    };
    public static readonly IReadOnlyDictionary<string, string> DueGroups = new Dictionary<string, string>
    {
        ["today"] = "Dzisiaj", ["overdue"] = "Zaległe", ["unclear"] = "Termin do doprecyzowania",
        ["future"] = "Zaplanowane później", ["none"] = "Bez terminu"
    };
}

public record ContactPlan(DateOnly? Date, string? Description, string? Action);
public record CustomerFields(string DisplayName, string? ContactPerson = null, string? Phone = null,
    string? Email = null, string? Source = null, string? OriginalNote = null);
public record CreateCustomer(Guid RequestId, CustomerFields Fields, bool IsSynthetic = false);
public record EditCustomer(Guid RequestId, long ExpectedVersion, CustomerFields Fields);
public record ChangeCustomerLifecycle(Guid RequestId, long ExpectedVersion);
public record TopicFields(string[] Products, string? Need, string State = "new", ContactPlan? NextContact = null);
public record CreateTopic(Guid RequestId, TopicFields Fields);
public record EditTopic(Guid RequestId, long ExpectedVersion, TopicFields Fields);
public record RecordContact(Guid RequestId, long ExpectedVersion, string? Note, string State, ContactPlan? NextContact);
public record CustomerDto(Guid Id, long Version, CustomerFields Fields, bool IsSynthetic, DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt = null, long? ArchivedBy = null);
public record TopicDto(Guid Id, Guid CustomerId, long Version, TopicFields Fields);
public record TopicSummary(Guid Id, string[] Products, string State, ContactPlan? NextContact);
public record CustomerSummaryRow(CustomerDto Customer, TopicSummary[] Topics, int TopicCount, DateTimeOffset? LastContact);
public record CustomerRow(CustomerDto Customer, TopicDto[] Topics, DateTimeOffset? LastContact);
public record CustomerDetail(CustomerDto Customer, TopicDto[] Topics);
public record CustomerChangeDto(Guid Id, long ActorId, DateTimeOffset RecordedAt, string Changes);
public record ContactEventDto(Guid Id, Guid TopicId, long ActorId, string ActorName, DateTimeOffset RecordedAt,
    string Kind, string? Note, string State, ContactPlan? NextContact, string? Changes);
public record QueueRow(CustomerDto Customer, TopicDto Topic, string Group);
public record Page<T>(T[] Items, int Total, int PageNumber, int PageSize);
public record SaveCustomerResult(CustomerDto Customer, CustomerDto[] PossibleDuplicates);
public record LoginRequest(string Role, string Pin);
public record PasswordLoginRequest(string Username, string Password, bool RememberMe = false);
public record SessionDto(long UserId, string Name, Guid? TeamId, string? TeamName, string? Role = null, string? DefaultShift = null, bool ProductionEnabled = false);
public record ApiError(int Status, string Title, Dictionary<string, string[]>? Errors = null);
