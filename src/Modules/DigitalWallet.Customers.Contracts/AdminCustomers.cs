namespace DigitalWallet.Customers.Contracts;

public sealed record AdminCustomer(Guid Id, string FirstName, string LastName, string? PhoneNumber, string Status,
    string KycStatus, DateTimeOffset CreatedAt, Guid ConcurrencyVersion);
public sealed record CustomerSearch(string? Status = null, string? KycStatus = null, Guid? Id = null, string? Cursor = null, int? PageSize = null);
public sealed record CustomerSearchPage(IReadOnlyList<AdminCustomer> Items, string? NextCursor);
public interface IAdminCustomerReader
{
    Task<CustomerSearchPage> SearchAsync(CustomerSearch search, Guid actor, CancellationToken ct);
    Task<AdminCustomer?> GetAsync(Guid id, bool pii, CancellationToken ct);
    Task<IReadOnlyList<AdminCustomer>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, bool pii, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}
