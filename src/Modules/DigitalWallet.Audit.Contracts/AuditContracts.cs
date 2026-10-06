namespace DigitalWallet.Audit.Contracts;

public sealed record AuditFact(Guid ActorId, string ActorType, string Action, string ResourceType, string ResourceId,
    string Result, string CorrelationId, string? Reason = null, string? Changes = null);
public interface IAuditWriter { Task AppendAsync(AuditFact fact, CancellationToken ct); }
