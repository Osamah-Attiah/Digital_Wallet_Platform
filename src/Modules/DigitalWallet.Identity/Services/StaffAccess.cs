using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Identity.Services;

public sealed class StaffAccess(IdentityDbContext db, IOtpChallengeService otp, TimeProvider clock, EffectivePermissions effective) : IStaffAccess
{
    public Task<IReadOnlyList<string>> PermissionsAsync(Guid actor, CancellationToken ct) => effective.ResolveAsync(actor, ct);
    public Task<bool> HasAsync(Guid actor, string code, CancellationToken ct) =>
        (from ur in db.UserRoles join rp in db.RolePermissions on ur.RoleId equals rp.RoleId where ur.UserId == actor && rp.PermissionCode == code select rp).AnyAsync(ct);
    public Task<bool> RecentMfaAsync(Guid actor, Guid session, CancellationToken ct)
    {
        var binding = $"beneficiary:{session:N}:wallet:{session:N}:purpose:{OtpChallengePurpose.StaffStepUp}";
        var since = clock.GetUtcNow().AddMinutes(-5);
        return db.OtpChallenges.AsNoTracking().AnyAsync(x => x.UserId == actor && x.Purpose == OtpPurpose.StaffStepUp && x.ResourceBinding == binding && x.UsedAt >= since && x.UsedAt <= clock.GetUtcNow(), ct);
    }
    public Task<OtpChallengeIssued> IssueMfaAsync(Guid actor, Guid session, CancellationToken ct) => otp.CreateAsync(new(actor, session, session, OtpChallengePurpose.StaffStepUp), ct);
    public async Task<bool> VerifyMfaAsync(Guid actor, Guid session, Guid challenge, string code, CancellationToken ct) =>
        await otp.VerifyAsync(new(challenge, actor, session, session, OtpChallengePurpose.StaffStepUp, code), ct) == OtpVerificationStatus.Verified;
    public async Task<IReadOnlyList<AdminRole>> RolesAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(x => x.Name).Take(100).ToListAsync(ct);
        var permissions = await db.RolePermissions.AsNoTracking().ToListAsync(ct);
        return roles.Select(x => new AdminRole(x.Id, x.Name!, x.DisplayName, x.ConcurrencyStamp!, permissions.Where(p => p.RoleId == x.Id).Select(p => p.PermissionCode).Order().ToArray())).ToArray();
    }
    public async Task<IReadOnlyList<string>> CatalogAsync(CancellationToken ct) => await db.Permissions.AsNoTracking().OrderBy(x => x.Code).Select(x => x.Code).ToListAsync(ct);
    public async Task<AdminRole> CreateRoleAsync(Guid actor, RoleCreate input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') ||
            string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 128 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500 || input.Permissions == null || input.Permissions.Length > 100) throw new AdminException("VALIDATION_ERROR");
        var normalized = input.Name.ToUpperInvariant(); if (normalized == "CUSTOMER" || await db.Roles.AnyAsync(x => x.NormalizedName == normalized, ct)) throw new AdminException("ROLE_EXISTS", 409);
        var mine = await PermissionsAsync(actor, ct); var catalog = await CatalogAsync(ct); if (input.Permissions.Any(p => !mine.Contains(p) || !catalog.Contains(p))) throw new AdminException("PRIVILEGE_ESCALATION_FORBIDDEN", 403);
        var role = new ApplicationRole { Id = Guid.NewGuid(), Name = input.Name, NormalizedName = normalized, DisplayName = input.DisplayName, ConcurrencyStamp = Guid.NewGuid().ToString("D") }; db.Roles.Add(role);
        foreach (var code in input.Permissions.Distinct()) db.RolePermissions.Add(new() { RoleId = role.Id, PermissionCode = code });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { throw new AdminException("ROLE_EXISTS", 409); }
        return new(role.Id, role.Name, role.DisplayName, role.ConcurrencyStamp, input.Permissions.Distinct().ToArray());
    }
    public async Task<AdminRole> ChangeRoleAsync(Guid actor, Guid id, RoleChange input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 128 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500 || input.Permissions == null || input.Permissions.Length > 100 || string.IsNullOrEmpty(input.ConcurrencyVersion)) throw new AdminException("VALIDATION_ERROR");
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AdminException("ROLE_NOT_FOUND", 404);
        if (role.NormalizedName == "CUSTOMER" || await db.UserRoles.AnyAsync(x => x.UserId == actor && x.RoleId == id, ct)) throw new AdminException("SELF_PRIVILEGE_CHANGE_FORBIDDEN", 403);
        var mine = await PermissionsAsync(actor, ct); var catalog = await CatalogAsync(ct);
        if (input.Permissions.Any(p => !catalog.Contains(p) || !mine.Contains(p))) throw new AdminException("PRIVILEGE_ESCALATION_FORBIDDEN", 403);
        var prior = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(ct);
        // Removing permissions the actor cannot administer is also prohibited.
        if (prior.Any(p => !mine.Contains(p.PermissionCode))) throw new AdminException("PRIVILEGE_ESCALATION_FORBIDDEN", 403);
        db.Entry(role).Property(x => x.ConcurrencyStamp).OriginalValue = input.ConcurrencyVersion;
        role.ConcurrencyStamp = Guid.NewGuid().ToString("D"); role.DisplayName = input.DisplayName.Trim();
        var desired = input.Permissions.Distinct().ToArray();
        db.RolePermissions.RemoveRange(prior.Where(p => !desired.Contains(p.PermissionCode)));
        foreach (var code in desired.Where(p => !prior.Any(x => x.PermissionCode == p))) db.RolePermissions.Add(new RolePermission { RoleId = id, PermissionCode = code });
        var affected = await db.UserRoles.Where(x => x.RoleId == id).Select(x => x.UserId).ToListAsync(ct);
        foreach (var user in await db.Users.Where(x => affected.Contains(x.Id)).ToListAsync(ct)) user.AuthorizationVersion++;
        var tokens = await db.RefreshTokens.Where(x => affected.Contains(x.UserId) && x.RevokedAt == null).ToListAsync(ct);
        foreach (var token in tokens) { token.RevokedAt = clock.GetUtcNow(); token.RevocationReason = "RoleChanged"; }
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new AdminException("CONCURRENCY_CONFLICT", 409); }
        return new(role.Id, role.Name!, role.DisplayName, role.ConcurrencyStamp, desired);
    }
    public async Task<int> AssignAsync(Guid actor, Guid target, StaffRoleChange input, CancellationToken ct)
    {
        if (actor == target) throw new AdminException("SELF_PRIVILEGE_CHANGE_FORBIDDEN", 403);
        if (input.Roles == null || input.Roles.Length > 10 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500) throw new AdminException("VALIDATION_ERROR");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == target, ct) ?? throw new AdminException("STAFF_NOT_FOUND", 404);
        var ids = input.Roles.Distinct().ToArray(); var roles = await db.Roles.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (roles.Count != ids.Length || roles.Any(x => x.NormalizedName == "CUSTOMER")) throw new AdminException("VALIDATION_ERROR");
        var mine = await PermissionsAsync(actor, ct);
        var old = await db.UserRoles.Where(x => x.UserId == target).ToListAsync(ct);
        var allRoles = old.Select(x => x.RoleId).Concat(ids).ToArray();
        if (await db.RolePermissions.AnyAsync(x => allRoles.Contains(x.RoleId) && !mine.Contains(x.PermissionCode), ct)) throw new AdminException("PRIVILEGE_ESCALATION_FORBIDDEN", 403);
        db.Entry(user).Property(x => x.AuthorizationVersion).OriginalValue = input.ConcurrencyVersion; user.AuthorizationVersion++;
        db.UserRoles.RemoveRange(old.Where(x => !ids.Contains(x.RoleId)));
        foreach (var id in ids.Where(x => !old.Any(r => r.RoleId == x))) db.UserRoles.Add(new ApplicationUserRole { UserId = target, RoleId = id });
        foreach (var t in await db.RefreshTokens.Where(x => x.UserId == target && x.RevokedAt == null).ToListAsync(ct)) { t.RevokedAt = clock.GetUtcNow(); t.RevocationReason = "StaffRolesChanged"; }
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new AdminException("CONCURRENCY_CONFLICT", 409); }
        return user.AuthorizationVersion;
    }
}
