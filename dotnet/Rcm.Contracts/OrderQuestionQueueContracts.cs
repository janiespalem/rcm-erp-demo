namespace Rcm.Contracts;

public sealed record OrderQuestionQueueRow(long Id, long OrderId, string? OrderNumber, string Client,
    DateOnly? Deadline, DateTimeOffset? ArchivedAt, string QuestionText, string? AnswerText,
    string Status, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);
