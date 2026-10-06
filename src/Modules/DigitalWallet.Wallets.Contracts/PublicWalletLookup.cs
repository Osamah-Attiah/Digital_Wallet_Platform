namespace DigitalWallet.Wallets.Contracts;

/// <summary>Safe, cross-module projection of a wallet resolved from its public handle.</summary>
public sealed record PublicWalletLookup(Guid WalletId, Guid OwnerCustomerId, bool IsEligibleToReceive);

public interface IPublicWalletLookup
{
    Task<PublicWalletLookup?> FindByPublicHandleAsync(string publicHandle, CancellationToken cancellationToken);
}
