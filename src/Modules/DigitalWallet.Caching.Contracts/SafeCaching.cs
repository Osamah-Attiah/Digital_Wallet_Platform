namespace DigitalWallet.Caching.Contracts;

// The closed contract deliberately cannot accept arbitrary objects, balances, tokens or PII.
public sealed record PermissionSnapshot(string[] Codes);
public sealed record SettingSnapshot(string Key, string Value, Guid ConcurrencyVersion);
public sealed record DashboardSnapshot(long CustomerCount, long PendingReversals, long PendingMessages,
    long FailedMessages, long PublishAttempts, double OldestUnpublishedSeconds, uint? DlqCount, bool BrokerAvailable);
public sealed record CacheRead<T>(T Value, DateTimeOffset AsOf, bool FromCache);

public interface ISafeCache
{
    bool Enabled { get; }
    Task<CacheRead<PermissionSnapshot>> PermissionsAsync(Guid user, int authorizationVersion, Func<CancellationToken, Task<PermissionSnapshot>> load, CancellationToken ct);
    Task<CacheRead<SettingSnapshot[]>> SettingsAsync(string committedVersion, Func<CancellationToken, Task<SettingSnapshot[]>> load, CancellationToken ct);
    Task<CacheRead<DashboardSnapshot>> DashboardAsync(Func<CancellationToken, Task<DashboardSnapshot>> load, CancellationToken ct);
}

public sealed record RateDecision(bool Allowed, int RetryAfterSeconds, bool Fallback);
public sealed record RatePolicy(string Name, int PermitLimit, TimeSpan Window);
public interface IDistributedRateCoordinator
{
    Task<RateDecision> AcquireAsync(RatePolicy policy, string subject, CancellationToken ct);
}
