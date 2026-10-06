using Microsoft.AspNetCore.Identity;

namespace DigitalWallet.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string Contact { get; set; } = string.Empty;
    public string NormalizedContact { get; set; } = string.Empty;
    public string Locale { get; set; } = "ar-SA";
    public bool AcceptedTerms { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int AuthorizationVersion { get; set; } = 1;
    public ICollection<ApplicationUserRole> UserRoles { get; } = new List<ApplicationUserRole>();
}

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public sealed class ApplicationUserRole : IdentityUserRole<Guid> { }

public sealed class Permission
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = string.Empty;
    public ApplicationRole Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ClientDeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? LastIpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FamilyId { get; set; }
    public int AuthorizationVersion { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string SecurityStamp { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Device Device { get; set; } = null!;
}

public enum OtpPurpose { Registration = 1, PasswordReset = 2, BeneficiaryActivation = 3, TransferConfirmation = 4, ReversalApproval = 5, StaffStepUp = 6 }

public sealed class OtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public OtpPurpose Purpose { get; set; }
    public string Target { get; set; } = string.Empty;
    /// <summary>Canonical non-secret resource binding for non-contact OTP purposes.</summary>
    public string? ResourceBinding { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public int AttemptCount { get; set; }
    public int ResendCount { get; set; }
    public ApplicationUser User { get; set; } = null!;
}
