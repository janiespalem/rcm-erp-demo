namespace Rcm.Contracts;

public record OrderOperationRow(long Id, string Name, string? Department, string? Responsible,
    int? Sequence, string? Status, double? ActualHours);
public record OrderQuestionDto(long Id, long OrderId, string QuestionText, string? AnswerText,
    string Status, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);
public record SetOrderHours(Guid RequestId, double ActualHours);
public record AskOrderQuestion(Guid RequestId, string QuestionText);
public record AnswerOrderQuestion(Guid RequestId, string AnswerText);
