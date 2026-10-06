using DigitalWallet.SharedKernel.Observability;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DigitalWallet.Identity.Persistence;
using IdentityContracts = DigitalWallet.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DigitalWallet.Identity.Services;

public interface ISecretHasher { string Hash(string value, string key); bool Verify(string value, string expectedHash, string key); }
public sealed class SecretHasher : ISecretHasher
{
    public string Hash(string value, string key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
    public bool Verify(string value, string expectedHash, string key) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(value, key)), Convert.FromHexString(expectedHash));
}
public interface IOtpCodeGenerator { string Create(); }
public sealed class OtpCodeGenerator : IOtpCodeGenerator { public string Create() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"); }
public interface IOtpSender { Task SendAsync(Guid challengeId, string target, string code, OtpPurpose purpose, CancellationToken cancellationToken); }
public sealed class NullOtpSender : IOtpSender { public Task SendAsync(Guid challengeId, string target, string code, OtpPurpose purpose, CancellationToken cancellationToken) => Task.CompletedTask; }

public interface IIdentityService
{
    Task<RegisterResult> RegisterAsync(RegisterRequest request, HttpContext httpContext, CancellationToken cancellationToken);
    Task<AuthResult?> VerifyRegistrationAsync(VerifyOtpRequest request, HttpContext httpContext, CancellationToken cancellationToken);
    Task<AuthResult?> LoginAsync(LoginRequest request, HttpContext httpContext, CancellationToken cancellationToken);
    Task<AuthResult?> RefreshAsync(RefreshRequest request, HttpContext httpContext, CancellationToken cancellationToken);
    Task LogoutAsync(string token, CancellationToken cancellationToken);
    Task LogoutAllAsync(Guid userId, CancellationToken cancellationToken);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);
    Task<bool> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceResponse>> ListDevicesAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}

public sealed class IdentityService(IdentityDbContext context, UserManager<ApplicationUser> users, ISecretHasher hasher, IOtpCodeGenerator otpCodes, IOtpSender otpSender, IOptions<IdentityOptions> options, TimeProvider timeProvider) : IIdentityService, IdentityContracts.IOtpChallengeService
{
    private readonly IdentityOptions _options = options.Value;

    public async Task<IdentityContracts.OtpChallengeIssued> CreateAsync(IdentityContracts.CreateOtpChallenge request, CancellationToken ct)
    {
        if (request.OwnerIdentityUserId == Guid.Empty || request.ResourceId == Guid.Empty || request.TargetWalletId == Guid.Empty || !ValidBinding(request.Purpose, request.Transfer))
            throw new ArgumentException("OTP resource binding is required.", nameof(request));
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(request.OwnerIdentityUserId, ct);
        if (user is null || !user.EmailConfirmed) throw new InvalidOperationException("OTP owner is unavailable.");
        var binding = Binding(request.ResourceId, request.TargetWalletId, request.Purpose, request.Transfer);
        var challenge = await CreateChallengeAsync(user, Purpose(request.Purpose), user.NormalizedContact, binding, ct);
        await transaction.CommitAsync(ct);
        return new IdentityContracts.OtpChallengeIssued(challenge.Id, challenge.ExpiresAt, challenge.CreatedAt);
    }

    public Task<IdentityContracts.OtpVerificationStatus> VerifyAsync(IdentityContracts.VerifyOtpChallenge request, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Identity, OperationName.Request, async () => { var status = await VerifyAsyncCore(request, ct); if (status != IdentityContracts.OtpVerificationStatus.Verified) WalletTelemetry.Security(status == IdentityContracts.OtpVerificationStatus.Expired ? SecurityEvent.OtpExpired : status == IdentityContracts.OtpVerificationStatus.AttemptsExceeded ? SecurityEvent.OtpLocked : SecurityEvent.OtpInvalid, TelemetryModule.Identity); return status; }, status => status == IdentityContracts.OtpVerificationStatus.Verified ? OperationOutcome.Completed : OperationOutcome.Rejected);
    private async Task<IdentityContracts.OtpVerificationStatus> VerifyAsyncCore(IdentityContracts.VerifyOtpChallenge request, CancellationToken ct)
    {
        if (request.ChallengeId == Guid.Empty || request.OwnerIdentityUserId == Guid.Empty || request.ResourceId == Guid.Empty || request.TargetWalletId == Guid.Empty || string.IsNullOrWhiteSpace(request.Code) || !ValidBinding(request.Purpose, request.Transfer))
            return IdentityContracts.OtpVerificationStatus.Invalid;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (await LockUserAsync(request.OwnerIdentityUserId, ct) is null) return IdentityContracts.OtpVerificationStatus.Invalid;
        var binding = Binding(request.ResourceId, request.TargetWalletId, request.Purpose, request.Transfer);
        var challenge = await context.OtpChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId &&
            x.UserId == request.OwnerIdentityUserId && x.Purpose == Purpose(request.Purpose) && x.ResourceBinding == binding, ct);
        var now = timeProvider.GetUtcNow();
        if (challenge is null || challenge.UsedAt is not null || challenge.SupersededAt is not null)
        {
            await transaction.CommitAsync(ct);
            return IdentityContracts.OtpVerificationStatus.Invalid;
        }
        if (challenge.ExpiresAt <= now)
        {
            await transaction.CommitAsync(ct);
            return IdentityContracts.OtpVerificationStatus.Expired;
        }
        if (challenge.AttemptCount >= _options.Otp.MaxAttempts)
        {
            await transaction.CommitAsync(ct);
            return IdentityContracts.OtpVerificationStatus.AttemptsExceeded;
        }
        challenge.AttemptCount++;
        if (!hasher.Verify(request.Code, challenge.CodeHash, _options.Otp.HashKey))
        {
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return challenge.AttemptCount >= _options.Otp.MaxAttempts
                ? IdentityContracts.OtpVerificationStatus.AttemptsExceeded
                : IdentityContracts.OtpVerificationStatus.Invalid;
        }
        challenge.UsedAt = now;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return IdentityContracts.OtpVerificationStatus.Verified;
    }

    public async Task<IdentityContracts.OtpVerificationReceiptStatus> GetVerificationReceiptAsync(IdentityContracts.VerifyOtpChallenge request, CancellationToken ct)
    {
        if (request.ChallengeId == Guid.Empty || request.OwnerIdentityUserId == Guid.Empty || request.ResourceId == Guid.Empty || request.TargetWalletId == Guid.Empty || !ValidBinding(request.Purpose, request.Transfer))
            return IdentityContracts.OtpVerificationReceiptStatus.NotVerified;
        var binding = Binding(request.ResourceId, request.TargetWalletId, request.Purpose, request.Transfer);
        var verified = await context.OtpChallenges.AsNoTracking().AnyAsync(x => x.Id == request.ChallengeId &&
            x.UserId == request.OwnerIdentityUserId && x.Purpose == Purpose(request.Purpose) &&
            x.ResourceBinding == binding && x.UsedAt != null, ct);
        return verified ? IdentityContracts.OtpVerificationReceiptStatus.Verified : IdentityContracts.OtpVerificationReceiptStatus.NotVerified;
    }
    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, HttpContext httpContext, CancellationToken ct)
    {
        var contact = NormalizeContact(request.Email);
        if (await users.Users.AnyAsync(x => x.NormalizedContact == contact, ct)) return new RegisterResult(null, false);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = contact, Email = contact, Contact = request.Email.Trim(), NormalizedContact = contact, Locale = string.IsNullOrWhiteSpace(request.Locale) ? "ar-SA" : request.Locale, AcceptedTerms = request.AcceptTerms };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded) return new RegisterResult(null, false);
        await EnsureCustomerRoleAsync(user, ct);
        var challenge = await CreateChallengeAsync(user, OtpPurpose.Registration, contact, ct);
        return new RegisterResult(challenge.Id, true);
    }

    public async Task<AuthResult?> VerifyRegistrationAsync(VerifyOtpRequest request, HttpContext httpContext, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var userId = await context.OtpChallenges.Where(x => x.Id == request.ChallengeId).Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(ct);
        if (userId is null || await LockUserAsync(userId.Value, ct) is null) { WalletTelemetry.Security(SecurityEvent.OtpInvalid, TelemetryModule.Identity); return null; }
        var challenge = await VerifyChallengeAsync(request.ChallengeId, request.Code, OtpPurpose.Registration, ct);
        if (challenge is null) { await transaction.CommitAsync(ct); return null; }
        var user = await users.FindByIdAsync(challenge.UserId.ToString());
        if (user is null) return null;
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        var auth = await IssueSessionAsync(user, request.Device, httpContext, ct);
        await transaction.CommitAsync(ct);
        return auth;
    }

    public async Task<AuthResult?> LoginAsync(LoginRequest request, HttpContext httpContext, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var userId = await users.Users.Where(x => x.NormalizedContact == NormalizeContact(request.Email)).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var user = userId is null ? null : await LockUserAsync(userId.Value, ct);
        if (user is null || !user.EmailConfirmed || await users.IsLockedOutAsync(user) || !await users.CheckPasswordAsync(user, request.Password))
        {
            WalletTelemetry.Security(SecurityEvent.LoginFailed, TelemetryModule.Identity);
            if (user is not null) { await users.AccessFailedAsync(user); if (await users.IsLockedOutAsync(user)) WalletTelemetry.Security(SecurityEvent.LoginLockout, TelemetryModule.Identity); }
            await transaction.CommitAsync(ct);
            return null;
        }
        await users.ResetAccessFailedCountAsync(user);
        var auth = await IssueSessionAsync(user, request.Device, httpContext, ct);
        await transaction.CommitAsync(ct);
        return auth;
    }

    public async Task<AuthResult?> RefreshAsync(RefreshRequest request, HttpContext httpContext, CancellationToken ct)
    {
        var hash = hasher.Hash(request.RefreshToken, _options.TokenHashKey);
        // Lock the owner before reading mutable session state. Every session writer uses
        // this same lock, including replay revocation of descendants in the family.
        var userId = await context.RefreshTokens.Where(x => x.TokenHash == hash).Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(ct);
        if (userId is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(userId.Value, ct);
        if (user is null) return null;
        var current = await context.RefreshTokens.SingleAsync(x => x.TokenHash == hash, ct);
        if (current.RevokedAt is not null || current.UsedAt is not null || current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            WalletTelemetry.Security(SecurityEvent.RefreshReuse, TelemetryModule.Identity);
            await RevokeFamilyAsync(current.UserId, current.FamilyId, "ReuseDetected", ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        if (!user.EmailConfirmed || await users.IsLockedOutAsync(user) ||
            current.SecurityStamp != user.SecurityStamp || current.AuthorizationVersion != user.AuthorizationVersion ||
            !await context.Devices.AnyAsync(x => x.Id == current.DeviceId && x.UserId == user.Id && x.RevokedAt == null, ct))
        {
            WalletTelemetry.Security(SecurityEvent.SessionRejected, TelemetryModule.Identity);
            await RevokeFamilyAsync(current.UserId, current.FamilyId, "SecurityStateChanged", ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        current.UsedAt = DateTimeOffset.UtcNow;
        current.RevokedAt = current.UsedAt;
        current.RevocationReason = "Rotated";
        var next = NewRefreshToken(user, current.DeviceId, current.FamilyId);
        current.ReplacedByTokenId = next.Entity.Id;
        context.RefreshTokens.Add(next.Entity);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return CreateAuthResult(user, next, current.DeviceId);
    }

    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        var hash = hasher.Hash(token, _options.TokenHashKey);
        var owner = await context.RefreshTokens.Where(x => x.TokenHash == hash).Select(x => new { x.UserId, x.FamilyId }).SingleOrDefaultAsync(ct);
        if (owner is null) return;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockUserAsync(owner.UserId, ct);
        await RevokeFamilyAsync(owner.UserId, owner.FamilyId, "Logout", ct);
        await transaction.CommitAsync(ct);
    }
    public async Task LogoutAllAsync(Guid userId, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(userId, ct);
        if (user is null) return;
        user.SecurityStamp = Guid.NewGuid().ToString();
        await RevokeAllAsync(userId, "LogoutAll", ct);
        await transaction.CommitAsync(ct);
    }
    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        var userId = await users.Users.Where(x => x.NormalizedContact == NormalizeContact(request.Email)).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (userId is null) return;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(userId.Value, ct);
        if (user is not null && user.EmailConfirmed) await CreateChallengeAsync(user, OtpPurpose.PasswordReset, user.NormalizedContact, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<bool> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var userId = await context.OtpChallenges.Where(x => x.Id == request.ChallengeId).Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(ct);
        if (userId is null) return false;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (await LockUserAsync(userId.Value, ct) is null) return false;
        var challenge = await VerifyChallengeAsync(request.ChallengeId, request.Code, OtpPurpose.PasswordReset, ct);
        if (challenge is null) { await transaction.CommitAsync(ct); return false; }
        var user = await users.FindByIdAsync(challenge.UserId.ToString());
        if (user is null) return false;
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded) return false;
        await RevokeAllAsync(user.Id, "PasswordReset", ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    public async Task<IReadOnlyList<DeviceResponse>> ListDevicesAsync(Guid userId, CancellationToken ct) => await context.Devices.Where(x => x.UserId == userId).OrderByDescending(x => x.LastSeenAt).Select(x => new DeviceResponse(x.Id, x.Name, x.CreatedAt, x.LastSeenAt, x.RevokedAt, x.RefreshTokens.Any(t => t.RevokedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow))).ToListAsync(ct);
    public async Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (await LockUserAsync(userId, ct) is null) return false;
        var device = await context.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.UserId == userId, ct);
        if (device is null) return false;
        device.RevokedAt = DateTimeOffset.UtcNow;
        await RevokeDeviceTokensAsync(device.Id, "DeviceRevoked", ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    private async Task<AuthResult> IssueSessionAsync(ApplicationUser user, DeviceRequest deviceRequest, HttpContext httpContext, CancellationToken ct)
    {
        var clientId = string.IsNullOrWhiteSpace(deviceRequest.DeviceId) ? Guid.NewGuid().ToString("N") : deviceRequest.DeviceId.Trim();
        var device = await context.Devices.SingleOrDefaultAsync(x => x.UserId == user.Id && x.ClientDeviceId == clientId, ct) ?? new Device { UserId = user.Id, ClientDeviceId = clientId, Name = string.IsNullOrWhiteSpace(deviceRequest.Name) ? "Unknown device" : deviceRequest.Name.Trim() };
        device.LastSeenAt = DateTimeOffset.UtcNow; device.LastIpAddress = httpContext.Connection.RemoteIpAddress?.ToString(); device.UserAgent = httpContext.Request.Headers.UserAgent.ToString(); device.RevokedAt = null;
        if (context.Entry(device).State == EntityState.Detached) context.Devices.Add(device);
        var refresh = NewRefreshToken(user, device.Id, Guid.NewGuid()); context.RefreshTokens.Add(refresh.Entity); await context.SaveChangesAsync(ct);
        return CreateAuthResult(user, refresh, device.Id);
    }
    private AuthResult CreateAuthResult(ApplicationUser user, RefreshTokenIssue refresh, Guid? deviceId)
    {
        var now = DateTimeOffset.UtcNow; var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Jwt.SigningKey));
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim("sid", refresh.Entity.FamilyId.ToString()), new Claim("token_version", user.SecurityStamp ?? string.Empty), new Claim("authorization_version", user.AuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim("auth_time", now.ToUnixTimeSeconds().ToString()), new Claim("amr", "pwd") };
        var jwt = new JwtSecurityToken(_options.Jwt.Issuer, _options.Jwt.Audience, claims, now.UtcDateTime, now.AddMinutes(_options.Jwt.AccessTokenLifetimeMinutes).UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AuthResult(new JwtSecurityTokenHandler().WriteToken(jwt), refresh.Plaintext, refresh.Entity.ExpiresAt, refresh.Entity.FamilyId, deviceId ?? refresh.Entity.DeviceId);
    }
    private RefreshTokenIssue NewRefreshToken(ApplicationUser user, Guid deviceId, Guid familyId)
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return new RefreshTokenIssue(new RefreshToken { UserId = user.Id, AuthorizationVersion = user.AuthorizationVersion, SecurityStamp = user.SecurityStamp ?? string.Empty, DeviceId = deviceId, FamilyId = familyId, TokenHash = hasher.Hash(plaintext, _options.TokenHashKey), ExpiresAt = DateTimeOffset.UtcNow.AddDays(_options.Jwt.RefreshTokenLifetimeDays) }, plaintext);
    }
    private Task<OtpChallenge> CreateChallengeAsync(ApplicationUser user, OtpPurpose purpose, string target, CancellationToken ct) => CreateChallengeAsync(user, purpose, target, null, ct);
    private async Task<OtpChallenge> CreateChallengeAsync(ApplicationUser user, OtpPurpose purpose, string target, string? resourceBinding, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var recent = await context.OtpChallenges.Where(x => x.UserId == user.Id && x.Purpose == purpose && x.Target == target && x.ResourceBinding == resourceBinding).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (purpose is OtpPurpose.TransferConfirmation or OtpPurpose.ReversalApproval && recent?.UsedAt is not null) return recent;
        if (recent is not null && now - recent.CreatedAt < TimeSpan.FromSeconds(_options.Otp.ResendCooldownSeconds)) { WalletTelemetry.Security(SecurityEvent.OtpThrottled, TelemetryModule.Identity); return recent; }
        var resendCount = await context.OtpChallenges.CountAsync(x => x.UserId == user.Id && x.Purpose == purpose && x.Target == target && x.ResourceBinding == resourceBinding && x.CreatedAt >= now.AddMinutes(-_options.Otp.LifetimeMinutes), ct);
        if (recent is not null && resendCount >= _options.Otp.MaxResends) { WalletTelemetry.Security(SecurityEvent.OtpThrottled, TelemetryModule.Identity); return recent; }
        var existing = await context.OtpChallenges.Where(x => x.UserId == user.Id && x.Purpose == purpose && x.Target == target && x.ResourceBinding == resourceBinding && x.UsedAt == null && x.SupersededAt == null && x.ExpiresAt > now).ToListAsync(ct);
        foreach (var item in existing) item.SupersededAt = now;
        var code = otpCodes.Create(); var challenge = new OtpChallenge { CreatedAt = now, UserId = user.Id, Purpose = purpose, Target = target, ResourceBinding = resourceBinding, CodeHash = hasher.Hash(code, _options.Otp.HashKey), ExpiresAt = now.AddMinutes(_options.Otp.LifetimeMinutes), ResendCount = resendCount };
        context.OtpChallenges.Add(challenge); await context.SaveChangesAsync(ct); await otpSender.SendAsync(challenge.Id, target, code, purpose, ct); return challenge;
    }
    private async Task<OtpChallenge?> VerifyChallengeAsync(Guid id, string code, OtpPurpose purpose, CancellationToken ct)
    {
        var item = await context.OtpChallenges.SingleOrDefaultAsync(x => x.Id == id && x.Purpose == purpose, ct);
        if (item is null || item.UsedAt is not null || item.SupersededAt is not null || item.ExpiresAt <= timeProvider.GetUtcNow() || item.AttemptCount >= _options.Otp.MaxAttempts)
        { WalletTelemetry.Security(item != null && item.ExpiresAt <= timeProvider.GetUtcNow() ? SecurityEvent.OtpExpired : item != null && item.AttemptCount >= _options.Otp.MaxAttempts ? SecurityEvent.OtpLocked : SecurityEvent.OtpInvalid, TelemetryModule.Identity); return null; }
        item.AttemptCount++;
        if (!hasher.Verify(code, item.CodeHash, _options.Otp.HashKey)) { WalletTelemetry.Security(item.AttemptCount >= _options.Otp.MaxAttempts ? SecurityEvent.OtpLocked : SecurityEvent.OtpInvalid, TelemetryModule.Identity); await context.SaveChangesAsync(ct); return null; }
        item.UsedAt = timeProvider.GetUtcNow(); await context.SaveChangesAsync(ct); return item;
    }
    private static bool ValidBinding(IdentityContracts.OtpChallengePurpose purpose, IdentityContracts.TransferOtpDetails? transfer) => purpose switch
    {
        IdentityContracts.OtpChallengePurpose.BeneficiaryActivation or IdentityContracts.OtpChallengePurpose.StaffStepUp => transfer is null,
        IdentityContracts.OtpChallengePurpose.TransferConfirmation or IdentityContracts.OtpChallengePurpose.ReversalApproval => transfer is { SenderWalletId: var sender, AmountMinor: > 0, FeeMinor: >= 0, Currency: "SAR" } && sender != Guid.Empty,
        _ => false
    };
    private static OtpPurpose Purpose(IdentityContracts.OtpChallengePurpose purpose) => purpose switch
    {
        IdentityContracts.OtpChallengePurpose.BeneficiaryActivation => OtpPurpose.BeneficiaryActivation,
        IdentityContracts.OtpChallengePurpose.TransferConfirmation => OtpPurpose.TransferConfirmation,
        IdentityContracts.OtpChallengePurpose.ReversalApproval => OtpPurpose.ReversalApproval,
        IdentityContracts.OtpChallengePurpose.StaffStepUp => OtpPurpose.StaffStepUp,
        _ => throw new ArgumentException("Unsupported OTP purpose.")
    };
    private static string Binding(Guid resourceId, Guid targetWalletId, IdentityContracts.OtpChallengePurpose purpose, IdentityContracts.TransferOtpDetails? transfer) =>
        transfer is null ? $"beneficiary:{resourceId:N}:wallet:{targetWalletId:N}:purpose:{purpose}" :
        "transfer:v1:" + Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { ResourceId = resourceId, TargetWalletId = targetWalletId, Purpose = purpose, Transfer = transfer })));

    private async Task EnsureCustomerRoleAsync(ApplicationUser user, CancellationToken ct) { if (!await context.Roles.AnyAsync(x => x.NormalizedName == "CUSTOMER", ct)) context.Roles.Add(new ApplicationRole { Id = Guid.NewGuid(), Name = "Customer", NormalizedName = "CUSTOMER", DisplayName = "Customer" }); await context.SaveChangesAsync(ct); var role = await context.Roles.SingleAsync(x => x.NormalizedName == "CUSTOMER", ct); if (!await context.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == role.Id, ct)) { context.UserRoles.Add(new ApplicationUserRole { UserId = user.Id, RoleId = role.Id }); await context.SaveChangesAsync(ct); } }
    private async Task RevokeFamilyAsync(Guid userId, Guid familyId, string reason, CancellationToken ct) { var tokens = await context.RefreshTokens.Where(x => x.UserId == userId && x.FamilyId == familyId && x.RevokedAt == null).ToListAsync(ct); foreach (var token in tokens) { token.RevokedAt = DateTimeOffset.UtcNow; token.RevocationReason = reason; } await context.SaveChangesAsync(ct); }
    private async Task RevokeAllAsync(Guid userId, string reason, CancellationToken ct) { var tokens = await context.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(ct); foreach (var token in tokens) { token.RevokedAt = DateTimeOffset.UtcNow; token.RevocationReason = reason; } await context.SaveChangesAsync(ct); }
    private async Task RevokeDeviceTokensAsync(Guid deviceId, string reason, CancellationToken ct) { var tokens = await context.RefreshTokens.Where(x => x.DeviceId == deviceId && x.RevokedAt == null).ToListAsync(ct); foreach (var token in tokens) { token.RevokedAt = DateTimeOffset.UtcNow; token.RevocationReason = reason; } await context.SaveChangesAsync(ct); }
    // PostgreSQL READ COMMITTED sees the previous writer's committed state after this
    // lock is acquired. Keeping one lock order (user, then sessions/devices/challenges)
    // serializes rotation, replay, login, logout, device revocation and password reset.
    private async Task<ApplicationUser?> LockUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await context.Users.FromSqlInterpolated($"SELECT * FROM identity.users WHERE \"Id\" = {userId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is not null) await context.Entry(user).ReloadAsync(ct);
        return user;
    }
    private static string NormalizeContact(string contact) => contact.Trim().ToUpperInvariant();
}

public sealed record DeviceRequest(string? DeviceId, string? Name);
public sealed record RegisterRequest(string Email, string Password, bool AcceptTerms, string? Locale, DeviceRequest Device);
public sealed record LoginRequest(string Email, string Password, DeviceRequest Device);
public sealed record VerifyOtpRequest(Guid ChallengeId, string Code, DeviceRequest Device);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(Guid ChallengeId, string Code, string NewPassword);
public sealed record RegisterResult(Guid? ChallengeId, bool Accepted);
public sealed record AuthResult(string AccessToken, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid SessionId, Guid DeviceId);
public sealed record DeviceResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt, bool HasActiveSession);
internal sealed record RefreshTokenIssue(RefreshToken Entity, string Plaintext);
