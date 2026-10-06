namespace DigitalWallet.Customers.Contracts;

public sealed record TransferCustomer(Guid CustomerId, bool IsEligible, string Tier, string MaskedName);
public interface ITransferCustomerReader
{
    Task<TransferCustomer?> GetAsync(Guid identityUserId, CancellationToken ct);
}
