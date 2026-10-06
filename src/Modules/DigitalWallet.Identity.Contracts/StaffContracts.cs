namespace DigitalWallet.Identity.Contracts;

public sealed record StaffSession(Guid ActorId, IReadOnlyList<string> Permissions, bool RecentMfa);
public sealed record AdminRole(Guid Id, string Name, string DisplayName, string ConcurrencyVersion, IReadOnlyList<string> Permissions);
public sealed record RoleCreate(string Name, string DisplayName, string[] Permissions, string Reason);
public sealed record RoleChange(string DisplayName, string[] Permissions, string ConcurrencyVersion, string Reason);
public sealed record StaffRoleChange(Guid[] Roles, int ConcurrencyVersion, string Reason);
public interface IStaffAccess
{
    Task<IReadOnlyList<string>> PermissionsAsync(Guid actor, CancellationToken ct);
    Task<bool> HasAsync(Guid actor, string code, CancellationToken ct);
    Task<bool> RecentMfaAsync(Guid actor, Guid session, CancellationToken ct);
    Task<OtpChallengeIssued> IssueMfaAsync(Guid actor, Guid session, CancellationToken ct);
    Task<bool> VerifyMfaAsync(Guid actor, Guid session, Guid challenge, string code, CancellationToken ct);
    Task<IReadOnlyList<AdminRole>> RolesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> CatalogAsync(CancellationToken ct);
    Task<AdminRole> CreateRoleAsync(Guid actor, RoleCreate input, CancellationToken ct);
    Task<AdminRole> ChangeRoleAsync(Guid actor, Guid id, RoleChange change, CancellationToken ct);
    Task<int> AssignAsync(Guid actor, Guid target, StaffRoleChange change, CancellationToken ct);
}
