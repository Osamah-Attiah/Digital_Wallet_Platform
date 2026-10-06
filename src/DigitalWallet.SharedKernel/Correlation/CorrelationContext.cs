namespace DigitalWallet.SharedKernel.Correlation;

public interface ICorrelationContext
{
    string Id { get; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    public string Id { get; private set; } = string.Empty;

    public void Set(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        Id = correlationId;
    }
}
