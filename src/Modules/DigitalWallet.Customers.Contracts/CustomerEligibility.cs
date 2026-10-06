namespace DigitalWallet.Customers.Contracts;

public sealed record CustomerEligibility(Guid CustomerId, bool IsEligible);
/// <summary>A deliberately minimal customer projection for recipient displays. It contains no identifier or contact data.</summary>
public sealed record MaskedCustomerName(string Value);

public interface ICustomerEligibilityReader
{
    Task<CustomerEligibility?> GetAsync(Guid customerId, CancellationToken cancellationToken);
    Task<CustomerEligibility?> GetByIdentityUserIdAsync(Guid identityUserId, CancellationToken cancellationToken);
}

public interface ICustomerRecipientReader
{
    Task<MaskedCustomerName?> GetMaskedNameAsync(Guid customerId, CancellationToken cancellationToken);
}
