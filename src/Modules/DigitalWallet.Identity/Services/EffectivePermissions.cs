using DigitalWallet.Caching.Contracts;
using DigitalWallet.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Identity.Services;

public sealed class AuthorizationReadContext
{
    public Guid Actor { get; set; }
    public int? ValidatedVersion { get; set; }
}

public sealed class EffectivePermissions(IdentityDbContext db, ISafeCache cache, AuthorizationReadContext authorization)
{
    private readonly Dictionary<Guid, Task<IReadOnlyList<string>>> request = new();
    public Task<IReadOnlyList<string>> ResolveAsync(Guid actor, CancellationToken ct)
    {
        // Mutators/report transactions always consult the owner directly; no network inside a transaction.
        if (db.Database.CurrentTransaction != null || !cache.Enabled) return ReadAsync(actor, ct);
        if (!request.TryGetValue(actor, out var value)) request[actor] = value = CachedAsync(actor, ct);
        return value;
    }
    public async Task<IReadOnlyList<string>> ReadAsync(Guid actor, CancellationToken ct) =>
        await (from ur in db.UserRoles.AsNoTracking() join rp in db.RolePermissions.AsNoTracking() on ur.RoleId equals rp.RoleId
               where ur.UserId == actor select rp.PermissionCode).Distinct().OrderBy(x => x).ToListAsync(ct);
    private async Task<IReadOnlyList<string>> CachedAsync(Guid actor, CancellationToken ct)
    {
        // JWT validation already checked this version against the PostgreSQL user and durable session.
        var version = authorization.Actor == actor && authorization.ValidatedVersion is { } verified ? verified : await Version(actor, ct);
        if (version == null) return Array.Empty<string>();
        var result = await cache.PermissionsAsync(actor, version.Value, async token => new((await ReadAsync(actor, token)).ToArray()), ct);
        // A role commit racing the Redis read cannot authorize with the previous version.
        if (await Version(actor, ct) != version) return await ReadAsync(actor, ct);
        return result.Value.Codes;
    }
    private Task<int?> Version(Guid actor, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(x => x.Id == actor).Select(x => (int?)x.AuthorizationVersion).SingleOrDefaultAsync(ct);
}
