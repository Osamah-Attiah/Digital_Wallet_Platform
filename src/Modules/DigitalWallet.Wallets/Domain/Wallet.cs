namespace DigitalWallet.Wallets.Domain;
public enum WalletStatus { Pending, Active, Frozen, Restricted, Closed }
public sealed class Wallet
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid CustomerId { get; set; }
    public string Currency { get; set; } = "SAR"; public WalletStatus Status { get; set; } = WalletStatus.Pending;
    public DateTimeOffset? LedgerProvisionedAt { get; set; }
    public string PublicHandle { get; set; } = string.Empty; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; public Guid ConcurrencyVersion { get; set; } = Guid.NewGuid();
    public ICollection<WalletStatusHistory> StatusHistory { get; } = new List<WalletStatusHistory>();
}
public sealed class WalletStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid WalletId { get; set; }
    public WalletStatus FromStatus { get; set; } public WalletStatus ToStatus { get; set; }
    public string Reason { get; set; } = string.Empty; public Guid? ActorIdentityUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
