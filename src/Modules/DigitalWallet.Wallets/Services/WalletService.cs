using System.Security.Cryptography;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DigitalWallet.Wallets.Services;
public sealed class WalletRuleException(string code, int statusCode, string message) : Exception(message) { public string Code { get; } = code; public int StatusCode { get; } = statusCode; }
public sealed record CreateWalletRequest(string? Currency = "SAR");
public sealed record WalletStateChangeRequest(string Reason, Guid ConcurrencyVersion);
public sealed record WalletResponse(Guid Id, Guid CustomerId, string Currency, WalletStatus Status, string PublicHandle, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid ConcurrencyVersion);
public interface IWalletService
{
    Task<WalletResponse> CreateAsync(Guid identityUserId, CreateWalletRequest request, CancellationToken ct);
    Task<IReadOnlyList<WalletResponse>> ListMineAsync(Guid identityUserId, CancellationToken ct);
    Task<WalletResponse> GetMineAsync(Guid id, Guid identityUserId, CancellationToken ct);
    Task<WalletResponse> FreezeAsync(Guid id, WalletStateChangeRequest request, Guid actor, CancellationToken ct);
    Task<WalletResponse> UnfreezeAsync(Guid id, WalletStateChangeRequest request, Guid actor, CancellationToken ct);
}
public sealed class WalletService(WalletsDbContext db, ICustomerEligibilityReader customers, WalletProvisioning provisioning) : IWalletService
{
    public async Task<WalletResponse> CreateAsync(Guid identityUserId, CreateWalletRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Currency, "SAR", StringComparison.OrdinalIgnoreCase)) throw Validation("Only SAR is supported in the MVP.");
        var customer = await customers.GetByIdentityUserIdAsync(identityUserId, ct) ?? throw new WalletRuleException("CUSTOMER_NOT_FOUND", 404, "Customer was not found.");
        if (!customer.IsEligible) throw new WalletRuleException("CUSTOMER_NOT_ACTIVE", 422, "An active, verified customer is required.");
        var existing = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customer.CustomerId && x.Currency == "SAR", ct);
        if (existing is not null) return Map(await provisioning.EnsureAsync(existing.Id, identityUserId, ct));
        var wallet = new Wallet { CustomerId = customer.CustomerId, Currency = "SAR", PublicHandle = Handle(), Status = WalletStatus.Pending };
        db.Wallets.Add(wallet);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "IX_wallets_CustomerId_Currency" })
        {
            db.ChangeTracker.Clear();
            wallet = await db.Wallets.AsNoTracking().SingleAsync(x => x.CustomerId == customer.CustomerId && x.Currency == "SAR", ct);
        }
        db.ChangeTracker.Clear();
        return Map(await provisioning.EnsureAsync(wallet.Id, identityUserId, ct));
    }
    public async Task<IReadOnlyList<WalletResponse>> ListMineAsync(Guid identityUserId, CancellationToken ct)
    {
        var customer = await customers.GetByIdentityUserIdAsync(identityUserId, ct) ?? throw new WalletRuleException("CUSTOMER_NOT_FOUND", 404, "Customer was not found.");
        return (await db.Wallets.AsNoTracking().Where(x => x.CustomerId == customer.CustomerId).OrderBy(x => x.CreatedAt).ToListAsync(ct)).Select(Map).ToList();
    }
    public async Task<WalletResponse> GetMineAsync(Guid id, Guid identityUserId, CancellationToken ct)
    {
        var customer = await customers.GetByIdentityUserIdAsync(identityUserId, ct) ?? throw new WalletRuleException("CUSTOMER_NOT_FOUND", 404, "Customer was not found.");
        var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customer.CustomerId, ct) ?? throw new WalletRuleException("WALLET_NOT_FOUND", 404, "Wallet was not found.");
        return Map(wallet);
    }
    public Task<WalletResponse> FreezeAsync(Guid id, WalletStateChangeRequest request, Guid actor, CancellationToken ct) => ChangeAsync(id, WalletStatus.Frozen, request, actor, ct);
    public Task<WalletResponse> UnfreezeAsync(Guid id, WalletStateChangeRequest request, Guid actor, CancellationToken ct) => ChangeAsync(id, WalletStatus.Active, request, actor, ct);
    private async Task<WalletResponse> ChangeAsync(Guid id, WalletStatus target, WalletStateChangeRequest request, Guid actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500 || request.ConcurrencyVersion == Guid.Empty) throw Validation("Reason and concurrency version are required.");
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new WalletRuleException("WALLET_NOT_FOUND", 404, "Wallet was not found.");
        if (target == WalletStatus.Active && wallet.LedgerProvisionedAt is null)
            throw new WalletRuleException("LEDGER_UNAVAILABLE", 503, "Wallet ledger provisioning is pending.");
        db.Entry(wallet).Property(x => x.ConcurrencyVersion).OriginalValue = request.ConcurrencyVersion;
        if ((target == WalletStatus.Frozen && wallet.Status != WalletStatus.Active) || (target == WalletStatus.Active && wallet.Status != WalletStatus.Frozen)) throw new WalletRuleException("STATE_CONFLICT", 409, "The requested state transition is invalid.");
        var from = wallet.Status; wallet.Status = target; wallet.UpdatedAt = DateTimeOffset.UtcNow; wallet.ConcurrencyVersion = Guid.NewGuid();
        db.WalletStatusHistory.Add(new WalletStatusHistory { WalletId = wallet.Id, FromStatus = from, ToStatus = target, Reason = request.Reason.Trim(), ActorIdentityUserId = actor });
        try { await db.SaveChangesAsync(ct); return Map(wallet); } catch (DbUpdateConcurrencyException) { throw new WalletRuleException("CONCURRENCY_CONFLICT", 409, "The resource was changed by another request."); }
    }
    private static WalletRuleException Validation(string message) => new("VALIDATION_ERROR", 400, message);
    private static WalletResponse Map(Wallet x) => new(x.Id, x.CustomerId, x.Currency, x.Status, x.PublicHandle, x.CreatedAt, x.UpdatedAt, x.ConcurrencyVersion);
    private static string Handle() => "w_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
}
