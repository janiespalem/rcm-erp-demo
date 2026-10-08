namespace Rcm.Contracts;

public record SaveOrderAsTemplate(Guid RequestId, string? Name = null, string? Category = null);
public record OrderSavedTemplate(long Id, string Name, string Category);
