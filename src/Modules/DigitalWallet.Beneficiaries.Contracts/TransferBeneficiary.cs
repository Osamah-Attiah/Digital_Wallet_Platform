namespace DigitalWallet.Beneficiaries.Contracts;

public sealed record TransferBeneficiary(Guid BeneficiaryId, Guid TargetWalletId, bool IsActive, string MaskedRecipientName);
public interface ITransferBeneficiaryReader
{
    Task<TransferBeneficiary?> ReadAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct);
    Task<TransferBeneficiary?> GetAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct);
}
