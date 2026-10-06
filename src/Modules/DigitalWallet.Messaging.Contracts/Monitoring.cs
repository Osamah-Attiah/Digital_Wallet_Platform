namespace DigitalWallet.Messaging.Contracts;

public sealed record MessagingStatus(long PendingCount, long FailedCount, long PublishAttempts, double OldestUnpublishedSeconds, uint? DlqCount, bool BrokerAvailable);
public interface IMessagingMonitor { Task<MessagingStatus> GetAsync(CancellationToken ct = default); }
