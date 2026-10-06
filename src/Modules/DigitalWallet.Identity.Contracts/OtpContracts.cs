namespace DigitalWallet.Identity.Contracts;

public enum OtpChallengePurpose { BeneficiaryActivation = 1, TransferConfirmation = 2, ReversalApproval = 3, StaffStepUp = 4 }
public enum OtpVerificationStatus { Verified = 1, Invalid = 2, Expired = 3, AttemptsExceeded = 4 }
public enum OtpVerificationReceiptStatus { NotVerified = 1, Verified = 2 }

/// <summary>The resource tuple is part of the OTP binding and must be supplied again at verification.</summary>
public sealed record CreateOtpChallenge(Guid OwnerIdentityUserId, Guid ResourceId, Guid TargetWalletId, OtpChallengePurpose Purpose, TransferOtpDetails? Transfer = null);
public sealed record VerifyOtpChallenge(Guid ChallengeId, Guid OwnerIdentityUserId, Guid ResourceId, Guid TargetWalletId, OtpChallengePurpose Purpose, string Code, TransferOtpDetails? Transfer = null);
public sealed record OtpChallengeIssued(Guid ChallengeId, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt = default);

public interface IOtpChallengeService
{
    Task<OtpChallengeIssued> CreateAsync(CreateOtpChallenge request, CancellationToken cancellationToken);
    Task<OtpVerificationStatus> VerifyAsync(VerifyOtpChallenge request, CancellationToken cancellationToken);
    /// <summary>Returns a durable consumed-OTP receipt only for this exact resource binding.</summary>
    Task<OtpVerificationReceiptStatus> GetVerificationReceiptAsync(VerifyOtpChallenge request, CancellationToken cancellationToken);
}

public sealed record TransferOtpDetails(Guid SenderWalletId, long AmountMinor, long FeeMinor, string Currency);
