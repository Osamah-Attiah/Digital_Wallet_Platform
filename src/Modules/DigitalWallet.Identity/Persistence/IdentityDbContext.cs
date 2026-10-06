using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Identity.Persistence;

public sealed class IdentityDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid, IdentityUserClaim<Guid>, ApplicationUserRole, IdentityUserLogin<Guid>, IdentityRoleClaim<Guid>, IdentityUserToken<Guid>>
{
    public const string Schema = "identity";
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<ApplicationUser>(b =>
        {
            b.ToTable("users"); b.Property(x=>x.AuthorizationVersion).IsConcurrencyToken(); b.Property(x => x.Contact).HasMaxLength(320).IsRequired(); b.Property(x => x.NormalizedContact).HasMaxLength(320).IsRequired();
            b.Property(x => x.Locale).HasMaxLength(16).IsRequired(); b.HasIndex(x => x.NormalizedContact).IsUnique(); b.HasIndex(x => x.NormalizedEmail).IsUnique();
        });
        builder.Entity<ApplicationRole>(b => { b.ToTable("roles"); b.Property(x => x.DisplayName).HasMaxLength(128).IsRequired(); });
        builder.Entity<ApplicationUserRole>(b => b.ToTable("user_roles"));
        builder.Entity<IdentityUserClaim<Guid>>(b => b.ToTable("user_claims"));
        builder.Entity<IdentityUserLogin<Guid>>(b => b.ToTable("user_logins"));
        builder.Entity<IdentityRoleClaim<Guid>>(b => b.ToTable("role_claims"));
        builder.Entity<IdentityUserToken<Guid>>(b => b.ToTable("user_tokens"));
        builder.Entity<Permission>(b => { b.ToTable("permissions"); b.HasKey(x => x.Code); b.Property(x => x.Code).HasMaxLength(128); b.Property(x => x.Description).HasMaxLength(256).IsRequired(); });
        builder.Entity<RolePermission>(b => { b.ToTable("role_permissions"); b.HasKey(x => new { x.RoleId, x.PermissionCode }); b.Property(x => x.PermissionCode).HasMaxLength(128); b.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId); b.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionCode); });
        builder.Entity<Device>(b => { b.ToTable("devices"); b.HasKey(x => x.Id); b.Property(x => x.ClientDeviceId).HasMaxLength(128).IsRequired(); b.Property(x => x.Name).HasMaxLength(128).IsRequired(); b.Property(x => x.UserAgent).HasMaxLength(512); b.Property(x => x.LastIpAddress).HasMaxLength(64); b.HasIndex(x => new { x.UserId, x.ClientDeviceId }).IsUnique(); b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<RefreshToken>(b => { b.ToTable("refresh_tokens"); b.HasKey(x => x.Id); b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired(); b.Property(x => x.RevocationReason).HasMaxLength(64); b.HasIndex(x => x.TokenHash).IsUnique(); b.HasIndex(x => new { x.UserId, x.FamilyId }); b.HasIndex(x => new { x.DeviceId, x.RevokedAt }); b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); b.HasOne(x => x.Device).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<OtpChallenge>(b => { b.ToTable("otp_challenges"); b.HasKey(x => x.Id); b.Property(x => x.Target).HasMaxLength(320).IsRequired(); b.Property(x => x.ResourceBinding).HasMaxLength(160); b.Property(x => x.CodeHash).HasMaxLength(128).IsRequired(); b.HasIndex(x => new { x.UserId, x.Purpose, x.Target, x.ExpiresAt }); b.HasIndex(x => new { x.UserId, x.Purpose, x.ResourceBinding, x.ExpiresAt }); b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
    }
}
