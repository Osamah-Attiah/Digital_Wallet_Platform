namespace DigitalWallet.Beneficiaries.Contracts;

public sealed record ActiveBeneficiary(Guid BeneficiaryId, Guid OwnerCustomerId, Guid TargetWalletId, string Nickname);

/// <summary>Transfers consume only a previously verified, immutable target reference.</summary>
public interface IBeneficiaryReader
{
    Task<ActiveBeneficiary?> GetActiveAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken cancellationToken);
}
