namespace DigitalWallet.Wallets.Contracts;

public sealed record TransferWallet(Guid WalletId, Guid CustomerId, string Currency, bool IsActive);
public interface ITransferWalletReader
{
    Task<TransferWallet?> FindOwnedAsync(Guid customerId, string currency, CancellationToken ct);
    Task<IReadOnlyList<TransferWallet>> GetAsync(Guid sourceId, Guid targetId, CancellationToken ct);
}
