using System.Text;
using System.Text.Json;
using DigitalWallet.Beneficiaries.Contracts;
using DigitalWallet.Beneficiaries.Domain;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Contracts;
using IdentityContracts = DigitalWallet.Identity.Contracts;
using DigitalWallet.Wallets.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace DigitalWallet.Beneficiaries.Application;

public sealed class BeneficiaryRuleException(string code, int statusCode, string message) : Exception(message)
{ public string Code { get; } = code; public int StatusCode { get; } = statusCode; }

public sealed record AddBeneficiaryRequest(string PublicHandle, string Nickname);
public sealed record VerifyBeneficiaryRequest(string Code, Guid? ConcurrencyVersion = null);
public sealed record ActivateBeneficiaryRequest(Guid? ConcurrencyVersion = null);
public sealed record BeneficiaryResponse(Guid Id, string Nickname, string MaskedRecipientName, BeneficiaryStatus Status, DateTimeOffset? VerifiedAt, DateTimeOffset? ActivateAfter, DateTimeOffset? ActivatedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid ConcurrencyVersion);
public sealed record BeneficiaryPage(IReadOnlyList<BeneficiaryResponse> Items, string? NextCursor);

public interface IBeneficiaryService
{
    Task<BeneficiaryResponse> AddAsync(Guid ownerIdentityUserId, AddBeneficiaryRequest request, CancellationToken ct);
    Task<BeneficiaryResponse> ResendOtpAsync(Guid ownerIdentityUserId, Guid beneficiaryId, CancellationToken ct);
    Task<BeneficiaryResponse> VerifyAsync(Guid ownerIdentityUserId, Guid beneficiaryId, VerifyBeneficiaryRequest request, CancellationToken ct);
    Task<BeneficiaryResponse> ActivateAsync(Guid ownerIdentityUserId, Guid beneficiaryId, ActivateBeneficiaryRequest request, CancellationToken ct);
    Task<BeneficiaryPage> ListAsync(Guid ownerIdentityUserId, string? cursor, int? pageSize, CancellationToken ct);
    Task<BeneficiaryResponse> GetAsync(Guid ownerIdentityUserId, Guid beneficiaryId, CancellationToken ct);
    Task DeleteAsync(Guid ownerIdentityUserId, Guid beneficiaryId, Guid? concurrencyVersion, CancellationToken ct);
}

public sealed class BeneficiaryService(BeneficiariesDbContext db, ICustomerEligibilityReader customers, ICustomerRecipientReader recipientCustomers, IPublicWalletLookup wallets,
    IdentityContracts.IOtpChallengeService otp, TimeProvider timeProvider, IOptions<BeneficiariesOptions> options) : IBeneficiaryService, IBeneficiaryReader
{
    public async Task<BeneficiaryResponse> AddAsync(Guid ownerIdentityUserId, AddBeneficiaryRequest request, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        if (!owner.IsEligible) throw new BeneficiaryRuleException("CUSTOMER_NOT_ACTIVE", 422, "An active, verified customer is required.");
        if (string.IsNullOrWhiteSpace(request.PublicHandle) || request.PublicHandle.Trim().Length > 32 || string.IsNullOrWhiteSpace(request.Nickname) || request.Nickname.Trim().Length > 100)
            throw Validation();
        var target = await wallets.FindByPublicHandleAsync(request.PublicHandle, ct);
        if (target is null) throw new BeneficiaryRuleException("RECIPIENT_NOT_FOUND", 404, "Recipient was not found.");
        if (target.OwnerCustomerId == owner.CustomerId) throw new BeneficiaryRuleException("SELF_BENEFICIARY_NOT_ALLOWED", 422, "A customer cannot add their own wallet.");
        if (!target.IsEligibleToReceive) throw new BeneficiaryRuleException("RECIPIENT_NOT_FOUND", 404, "Recipient was not found.");
        var recipient = await recipientCustomers.GetMaskedNameAsync(target.OwnerCustomerId, ct);
        if (recipient is null) throw new BeneficiaryRuleException("RECIPIENT_NOT_FOUND", 404, "Recipient was not found.");

        // The unique live-owner/target constraint is the idempotency key. A retry after
        // an Identity failure returns this same aggregate and finishes OTP issuance.
        var beneficiary = await db.Beneficiaries.SingleOrDefaultAsync(x => x.OwnerCustomerId == owner.CustomerId && x.TargetWalletId == target.WalletId && x.DeletedAt == null, ct);
        if (beneficiary is null)
        {
            beneficiary = Beneficiary.Create(owner.CustomerId, target.WalletId, request.Nickname.Trim(), recipient.Value, timeProvider.GetUtcNow());
            db.Beneficiaries.Add(beneficiary);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                db.Entry(beneficiary).State = EntityState.Detached;
                beneficiary = await db.Beneficiaries.SingleAsync(x => x.OwnerCustomerId == owner.CustomerId && x.TargetWalletId == target.WalletId && x.DeletedAt == null, ct);
            }
        }
        if (beneficiary.Status == BeneficiaryStatus.PendingVerification && beneficiary.OtpChallengeId is null)
        {
            try { await EnsureOtpAsync(ownerIdentityUserId, beneficiary, ct); }
            catch (BeneficiaryRuleException ex) when (ex.Code == "CONCURRENCY_CONFLICT")
            {
                // Another identical add completed the final local recovery step. Return
                // its aggregate instead of turning a safe idempotent retry into a 409.
                db.ChangeTracker.Clear();
                beneficiary = await db.Beneficiaries.SingleAsync(x => x.OwnerCustomerId == owner.CustomerId && x.TargetWalletId == target.WalletId && x.DeletedAt == null, ct);
            }
        }
        return Map(beneficiary);
    }

    public async Task<BeneficiaryResponse> ResendOtpAsync(Guid ownerIdentityUserId, Guid beneficiaryId, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        var beneficiary = await OwnedAsync(owner.CustomerId, beneficiaryId, ct);
        if (beneficiary.Status != BeneficiaryStatus.PendingVerification) throw StateConflict();
        await EnsureOtpAsync(ownerIdentityUserId, beneficiary, ct, force: true);
        return Map(beneficiary);
    }

    public async Task<BeneficiaryResponse> VerifyAsync(Guid ownerIdentityUserId, Guid beneficiaryId, VerifyBeneficiaryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length > 32) throw Validation();
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        var beneficiary = await OwnedAsync(owner.CustomerId, beneficiaryId, ct);
        SetVersion(beneficiary, request.ConcurrencyVersion);
        if (beneficiary.Status != BeneficiaryStatus.PendingVerification || beneficiary.OtpChallengeId is null) throw StateConflict();
        var otpRequest = OtpRequest(beneficiary, ownerIdentityUserId, request.Code.Trim());
        // Identity is the durable source for consuming a code. If its commit succeeds but
        // our local transition does not, this exact binding can finish on a retry.
        var receipt = await otp.GetVerificationReceiptAsync(otpRequest, ct);
        if (receipt != IdentityContracts.OtpVerificationReceiptStatus.Verified)
        {
            var result = await otp.VerifyAsync(otpRequest, ct);
            if (result != IdentityContracts.OtpVerificationStatus.Verified)
            {
                receipt = await otp.GetVerificationReceiptAsync(otpRequest, ct);
                if (receipt != IdentityContracts.OtpVerificationReceiptStatus.Verified) throw OtpFailure(result);
            }
        }
        try { beneficiary.Verify(timeProvider.GetUtcNow(), options.Value.CoolingPeriod); }
        catch (InvalidOperationException) { throw StateConflict(); }
        await SaveAsync(ct);
        return Map(beneficiary);
    }

    public async Task<BeneficiaryResponse> ActivateAsync(Guid ownerIdentityUserId, Guid beneficiaryId, ActivateBeneficiaryRequest request, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        var beneficiary = await OwnedAsync(owner.CustomerId, beneficiaryId, ct);
        SetVersion(beneficiary, request.ConcurrencyVersion);
        if (beneficiary.Status == BeneficiaryStatus.PendingVerification) throw new BeneficiaryRuleException("BENEFICIARY_NOT_VERIFIED", 409, "Beneficiary verification is required.");
        if (beneficiary.Status != BeneficiaryStatus.CoolingOff) throw StateConflict();
        if (beneficiary.ActivateAfter is null || timeProvider.GetUtcNow() < beneficiary.ActivateAfter)
            throw new BeneficiaryRuleException("COOLING_PERIOD_ACTIVE", 409, "The cooling period is still active.");
        try { beneficiary.Activate(timeProvider.GetUtcNow()); }
        catch (InvalidOperationException) { throw StateConflict(); }
        await SaveAsync(ct);
        return Map(beneficiary);
    }

    public async Task<BeneficiaryPage> ListAsync(Guid ownerIdentityUserId, string? cursor, int? pageSize, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        var size = Math.Clamp(pageSize ?? 20, 1, options.Value.MaxPageSize);
        var parsed = ParseCursor(cursor);
        var query = db.Beneficiaries.AsNoTracking().Where(x => x.OwnerCustomerId == owner.CustomerId && x.DeletedAt == null);
        if (parsed is not null) query = query.Where(x => x.CreatedAt < parsed.CreatedAt || (x.CreatedAt == parsed.CreatedAt && x.Id.CompareTo(parsed.Id) < 0));
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).ToListAsync(ct);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new BeneficiaryPage(rows.Select(Map).ToList(), hasMore && rows.Count > 0 ? Cursor(rows[^1]) : null);
    }

    public async Task<BeneficiaryResponse> GetAsync(Guid ownerIdentityUserId, Guid beneficiaryId, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        return Map(await OwnedAsync(owner.CustomerId, beneficiaryId, ct));
    }

    public async Task DeleteAsync(Guid ownerIdentityUserId, Guid beneficiaryId, Guid? concurrencyVersion, CancellationToken ct)
    {
        var owner = await OwnerAsync(ownerIdentityUserId, ct);
        var beneficiary = await OwnedAsync(owner.CustomerId, beneficiaryId, ct);
        SetVersion(beneficiary, concurrencyVersion);
        try { beneficiary.Delete(timeProvider.GetUtcNow()); }
        catch (InvalidOperationException) { throw NotFound(); }
        await SaveAsync(ct);
    }

    public async Task<ActiveBeneficiary?> GetActiveAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct)
    {
        var item = await db.Beneficiaries.AsNoTracking().Where(x => x.Id == beneficiaryId && x.OwnerCustomerId == ownerCustomerId && x.Status == BeneficiaryStatus.Active && x.DeletedAt == null)
            .Select(x => new { x.Id, x.OwnerCustomerId, x.TargetWalletId, x.Nickname }).SingleOrDefaultAsync(ct);
        return item is null ? null : new ActiveBeneficiary(item.Id, item.OwnerCustomerId, item.TargetWalletId, item.Nickname);
    }

    private async Task<CustomerEligibility> OwnerAsync(Guid identityUserId, CancellationToken ct) =>
        await customers.GetByIdentityUserIdAsync(identityUserId, ct) ?? throw new BeneficiaryRuleException("BENEFICIARY_NOT_FOUND", 404, "Beneficiary was not found.");
    private async Task<Beneficiary> OwnedAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct) =>
        await db.Beneficiaries.SingleOrDefaultAsync(x => x.Id == beneficiaryId && x.OwnerCustomerId == ownerCustomerId && x.DeletedAt == null, ct) ?? throw NotFound();
    private void SetVersion(Beneficiary item, Guid? version) { if (version is { } value && value != Guid.Empty) db.Entry(item).Property(x => x.ConcurrencyVersion).OriginalValue = value; }
    private async Task EnsureOtpAsync(Guid ownerIdentityUserId, Beneficiary beneficiary, CancellationToken ct, bool force = false)
    {
        if (!force && beneficiary.OtpChallengeId is not null) return;
        var issued = await otp.CreateAsync(new(ownerIdentityUserId, beneficiary.Id, beneficiary.TargetWalletId, IdentityContracts.OtpChallengePurpose.BeneficiaryActivation), ct);
        if (beneficiary.OtpChallengeId != issued.ChallengeId)
        {
            beneficiary.SetOtpChallenge(issued.ChallengeId, timeProvider.GetUtcNow());
            await SaveAsync(ct);
        }
    }
    private static IdentityContracts.VerifyOtpChallenge OtpRequest(Beneficiary item, Guid ownerIdentityUserId, string code) =>
        new(item.OtpChallengeId!.Value, ownerIdentityUserId, item.Id, item.TargetWalletId, IdentityContracts.OtpChallengePurpose.BeneficiaryActivation, code);
    private async Task SaveAsync(CancellationToken ct) { try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new BeneficiaryRuleException("CONCURRENCY_CONFLICT", 409, "The resource was changed by another request."); } }
    private static BeneficiaryRuleException OtpFailure(IdentityContracts.OtpVerificationStatus status) => status switch
    {
        IdentityContracts.OtpVerificationStatus.Expired => new("OTP_EXPIRED", 400, "OTP has expired."),
        IdentityContracts.OtpVerificationStatus.AttemptsExceeded => new("OTP_ATTEMPTS_EXCEEDED", 400, "OTP attempts have been exhausted."),
        _ => new("OTP_INVALID", 400, "OTP is invalid.")
    };
    private static BeneficiaryRuleException Validation() => new("VALIDATION_ERROR", 400, "The request is invalid.");
    private static BeneficiaryRuleException StateConflict() => new("STATE_CONFLICT", 409, "The requested state transition is invalid.");
    private static BeneficiaryRuleException NotFound() => new("BENEFICIARY_NOT_FOUND", 404, "Beneficiary was not found.");
    private static BeneficiaryResponse Map(Beneficiary x) => new(x.Id, x.Nickname, x.MaskedRecipientName, x.Status, x.VerifiedAt, x.ActivateAfter, x.ActivatedAt, x.CreatedAt, x.UpdatedAt, x.ConcurrencyVersion);
    private static string Cursor(Beneficiary x) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorValue(x.CreatedAt, x.Id))));
    private static CursorValue? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<CursorValue>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            return value is null || value.Id == Guid.Empty ? throw new FormatException() : value;
        }
        catch { throw Validation(); }
    }
    private sealed record CursorValue(DateTimeOffset CreatedAt, Guid Id);
}

public sealed class BeneficiariesOptions
{
    public const string SectionName = "Beneficiaries";
    [Range(0, 60 * 24 * 30)] public int CoolingPeriodMinutes { get; init; } = 30;
    [Range(1, 200)] public int MaxPageSize { get; init; } = 100;
    public TimeSpan CoolingPeriod => TimeSpan.FromMinutes(CoolingPeriodMinutes);
}
