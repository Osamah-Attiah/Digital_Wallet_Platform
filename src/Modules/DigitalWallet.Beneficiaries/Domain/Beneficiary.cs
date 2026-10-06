namespace DigitalWallet.Beneficiaries.Domain;

public enum BeneficiaryStatus { PendingVerification, CoolingOff, Active, Deleted }

public sealed class Beneficiary
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OwnerCustomerId { get; private set; }
    public Guid TargetWalletId { get; private set; }
    public string Nickname { get; private set; } = string.Empty;
    public string MaskedRecipientName { get; private set; } = string.Empty;
    public BeneficiaryStatus Status { get; private set; } = BeneficiaryStatus.PendingVerification;
    public Guid? OtpChallengeId { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public DateTimeOffset? ActivateAfter { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid ConcurrencyVersion { get; private set; } = Guid.NewGuid();

    private Beneficiary() { }

    public static Beneficiary Create(Guid ownerCustomerId, Guid targetWalletId, string nickname, string maskedRecipientName, DateTimeOffset now) => new()
    {
        OwnerCustomerId = ownerCustomerId,
        TargetWalletId = targetWalletId,
        Nickname = nickname,
        MaskedRecipientName = maskedRecipientName,
        CreatedAt = now,
        UpdatedAt = now
    };

    public void SetOtpChallenge(Guid challengeId, DateTimeOffset now)
    {
        OtpChallengeId = challengeId;
        Touch(now);
    }

    public void Verify(DateTimeOffset now, TimeSpan coolingPeriod)
    {
        if (Status != BeneficiaryStatus.PendingVerification) throw new InvalidOperationException("Invalid beneficiary transition.");
        Status = BeneficiaryStatus.CoolingOff;
        VerifiedAt = now;
        ActivateAfter = now.Add(coolingPeriod);
        Touch(now);
    }

    public void Activate(DateTimeOffset now)
    {
        if (Status != BeneficiaryStatus.CoolingOff || ActivateAfter is null || now < ActivateAfter) throw new InvalidOperationException("Invalid beneficiary transition.");
        Status = BeneficiaryStatus.Active;
        ActivatedAt = now;
        Touch(now);
    }

    public void Delete(DateTimeOffset now)
    {
        if (DeletedAt is not null) throw new InvalidOperationException("Invalid beneficiary transition.");
        Status = BeneficiaryStatus.Deleted;
        DeletedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) { UpdatedAt = now; ConcurrencyVersion = Guid.NewGuid(); }
}
