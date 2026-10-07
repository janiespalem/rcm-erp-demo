namespace Rcm.Contracts;

public record OrderAttachmentDto(long Id, long OrderId, string Filename, long? SizeBytes,
    string? MimeType, string? UploadedBy, DateTimeOffset UploadedAt);

public record RemoveOrderAttachment(Guid RequestId);
public record RemovedOrderAttachment(long Id);
