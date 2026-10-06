namespace DigitalWallet.Customers.Domain;

public enum CustomerStatus { PendingVerification, Active, Suspended, Blocked, Closed }
public enum KycStatus { NotStarted, Pending, Verified, Rejected, Expired }

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Logical cross-module identity reference; deliberately no FK to identity schema.
    public Guid IdentityUserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string PreferredLanguage { get; set; } = "ar-SA";
    public CustomerStatus Status { get; set; } = CustomerStatus.PendingVerification;
    public KycStatus KycStatus { get; set; } = KycStatus.NotStarted;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid ConcurrencyVersion { get; set; } = Guid.NewGuid();
    public ICollection<CustomerStatusHistory> StatusHistory { get; } = new List<CustomerStatusHistory>();
    public ICollection<KycStatusHistory> KycStatusHistory { get; } = new List<KycStatusHistory>();
}

public sealed class CustomerStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid CustomerId { get; set; }
    public CustomerStatus FromStatus { get; set; } public CustomerStatus ToStatus { get; set; }
    public string? Reason { get; set; } public Guid? ActorIdentityUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KycStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid CustomerId { get; set; }
    public KycStatus FromStatus { get; set; } public KycStatus ToStatus { get; set; }
    public string? Reason { get; set; } public Guid? ActorIdentityUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
