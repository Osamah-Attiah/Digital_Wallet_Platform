namespace DigitalWallet.Wallets.Contracts;

public sealed record AdminWallet(Guid Id, Guid CustomerId, string Currency, string Status, string PublicHandle, Guid ConcurrencyVersion);
public interface IAdminWalletReader
{
    Task<AdminWallet?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AdminWallet>> ForCustomerAsync(Guid id, CancellationToken ct);
}
