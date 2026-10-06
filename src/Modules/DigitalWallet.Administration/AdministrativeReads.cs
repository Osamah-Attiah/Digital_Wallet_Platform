using DigitalWallet.Caching.Contracts;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Transactions.Contracts;
using DigitalWallet.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Administration;

public sealed class AdministrativeReads(AdministrationDbContext db, ISafeCache cache, IAdminCustomerReader customers,
    IAdminTransactionReader transactions, IMessagingMonitor messaging, TimeProvider time)
{
    public async Task<IReadOnlyList<SettingSnapshot>> SettingsAsync(CancellationToken ct)
    {
        // Version is read on the writer after commit. Uncommitted values can never enter Redis.
        var allowed = db.Settings.AsNoTracking().Where(x => x.Key == "support.locale" || x.Key == "dashboard.refreshSeconds");
        async Task<SettingSnapshot[]> Load(CancellationToken token) => await allowed.OrderBy(x => x.Key).Select(x => new SettingSnapshot(x.Key, x.Value, x.ConcurrencyVersion)).ToArrayAsync(token);
        if (db.Database.CurrentTransaction != null || !cache.Enabled) return await Load(ct);
        var versions = await allowed.OrderBy(x => x.Key).Select(x => x.ConcurrencyVersion).ToArrayAsync(ct);
        var result = await cache.SettingsAsync(string.Join(':', versions), Load, ct);
        return result.Value;
    }
    public async Task<object> DashboardAsync(CancellationToken ct)
    {
        async Task<DashboardSnapshot> Load(CancellationToken token)
        {
            var count = await customers.CountAsync(token); var pending = await transactions.PendingReversalsAsync(token); var m = await messaging.GetAsync(token);
            return new(count, pending, m.PendingCount, m.FailedCount, m.PublishAttempts, m.OldestUnpublishedSeconds, m.DlqCount, m.BrokerAvailable);
        }
        var result = await cache.DashboardAsync(Load, ct); var v = result.Value;
        return new { customerCount = v.CustomerCount, pendingReversals = v.PendingReversals,
            messaging = new MessagingStatus(v.PendingMessages, v.FailedMessages, v.PublishAttempts, v.OldestUnpublishedSeconds, v.DlqCount, v.BrokerAvailable),
            asOf = result.AsOf, isStale = result.FromCache, ageSeconds = Math.Max(0, (time.GetUtcNow() - result.AsOf).TotalSeconds),
            source = result.FromCache ? "cache" : "owners", informationalOnly = true };
    }
}
