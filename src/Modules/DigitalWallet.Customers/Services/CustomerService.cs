using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Customers.Services;

public sealed class CustomerRuleException(string code, int statusCode, string message) : Exception(message)
{ public string Code { get; } = code; public int StatusCode { get; } = statusCode; }

public sealed record CreateProfileRequest(string FirstName, string LastName, string? PhoneNumber, string? PreferredLanguage);
public sealed record UpdateProfileRequest(string? FirstName, string? LastName, string? PhoneNumber, string? PreferredLanguage, Guid ConcurrencyVersion);
public sealed record StateChangeRequest(string Reason, Guid ConcurrencyVersion);
public sealed record CustomerResponse(Guid Id, Guid IdentityUserId, string FirstName, string LastName, string? PhoneNumber, string PreferredLanguage, CustomerStatus Status, KycStatus KycStatus, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid ConcurrencyVersion);
public sealed record KycResponse(Guid CustomerId, KycStatus Status, Guid ConcurrencyVersion);

public interface ICustomerService
{
    Task<CustomerResponse> CreateAsync(Guid identityUserId, CreateProfileRequest request, CancellationToken ct);
    Task<CustomerResponse> GetMineAsync(Guid identityUserId, CancellationToken ct);
    Task<CustomerResponse> UpdateMineAsync(Guid identityUserId, UpdateProfileRequest request, CancellationToken ct);
    Task<KycResponse> SubmitKycAsync(Guid identityUserId, StateChangeRequest request, CancellationToken ct);
    Task<KycResponse> GetKycAsync(Guid identityUserId, CancellationToken ct);
    Task<IReadOnlyList<CustomerResponse>> ListAsync(bool includePii, CancellationToken ct);
    Task<CustomerResponse> GetAsync(Guid id, bool includePii, CancellationToken ct);
    Task<CustomerResponse> ChangeStatusAsync(Guid id, CustomerStatus action, StateChangeRequest request, Guid actor, CancellationToken ct);
    Task<KycResponse> DecideKycAsync(Guid id, bool approved, StateChangeRequest request, Guid actor, CancellationToken ct);
}

public sealed class CustomerService(CustomersDbContext db) : ICustomerService
{
    public async Task<CustomerResponse> CreateAsync(Guid identityUserId, CreateProfileRequest request, CancellationToken ct)
    {
        ValidateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.PreferredLanguage);
        if (await db.Customers.AnyAsync(x => x.IdentityUserId == identityUserId, ct)) throw Conflict("PROFILE_EXISTS");
        var customer = new Customer { IdentityUserId = identityUserId, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), PhoneNumber = Clean(request.PhoneNumber), PreferredLanguage = request.PreferredLanguage?.Trim() ?? "ar-SA" };
        db.Customers.Add(customer);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw Conflict("PROFILE_EXISTS"); }
        return Map(customer, true);
    }
    public async Task<CustomerResponse> GetMineAsync(Guid identityUserId, CancellationToken ct) => Map(await Mine(identityUserId, ct), true);
    public async Task<CustomerResponse> UpdateMineAsync(Guid identityUserId, UpdateProfileRequest request, CancellationToken ct)
    {
        var customer = await Mine(identityUserId, ct); SetOriginalVersion(customer, request.ConcurrencyVersion);
        if (request.FirstName is not null) customer.FirstName = Required(request.FirstName, "firstName", 100);
        if (request.LastName is not null) customer.LastName = Required(request.LastName, "lastName", 100);
        if (request.PhoneNumber is not null) customer.PhoneNumber = Clean(request.PhoneNumber);
        if (request.PreferredLanguage is not null) customer.PreferredLanguage = Required(request.PreferredLanguage, "preferredLanguage", 16);
        Touch(customer); await Save(ct); return Map(customer, true);
    }
    public async Task<KycResponse> SubmitKycAsync(Guid identityUserId, StateChangeRequest request, CancellationToken ct)
    {
        var customer = await Mine(identityUserId, ct); RequireReason(request.Reason); SetOriginalVersion(customer, request.ConcurrencyVersion);
        if (customer.KycStatus is not (KycStatus.NotStarted or KycStatus.Rejected or KycStatus.Expired)) throw StateConflict();
        ChangeKyc(customer, KycStatus.Pending, request.Reason, identityUserId); Touch(customer); await Save(ct); return new(customer.Id, customer.KycStatus, customer.ConcurrencyVersion);
    }
    public async Task<KycResponse> GetKycAsync(Guid identityUserId, CancellationToken ct) { var c = await Mine(identityUserId, ct); return new(c.Id, c.KycStatus, c.ConcurrencyVersion); }
    public async Task<IReadOnlyList<CustomerResponse>> ListAsync(bool includePii, CancellationToken ct) => (await db.Customers.AsNoTracking().OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct)).Select(x => Map(x, false)).ToList();
    public async Task<CustomerResponse> GetAsync(Guid id, bool includePii, CancellationToken ct) => Map(await ById(id, ct), includePii);
    public async Task<CustomerResponse> ChangeStatusAsync(Guid id, CustomerStatus action, StateChangeRequest request, Guid actor, CancellationToken ct)
    {
        RequireReason(request.Reason); var customer = await ById(id, ct); SetOriginalVersion(customer, request.ConcurrencyVersion);
        var allowed = action switch { CustomerStatus.Suspended => customer.Status == CustomerStatus.Active, CustomerStatus.Blocked => customer.Status is CustomerStatus.Active or CustomerStatus.Suspended, CustomerStatus.Active => customer.Status == CustomerStatus.Blocked, _ => false };
        if (!allowed) throw StateConflict();
        var target = action == CustomerStatus.Active && customer.KycStatus != KycStatus.Verified ? CustomerStatus.PendingVerification : action;
        ChangeStatus(customer, target, request.Reason, actor); Touch(customer); await Save(ct); return Map(customer, false);
    }
    public async Task<KycResponse> DecideKycAsync(Guid id, bool approved, StateChangeRequest request, Guid actor, CancellationToken ct)
    {
        RequireReason(request.Reason); var customer = await ById(id, ct); SetOriginalVersion(customer, request.ConcurrencyVersion);
        if (customer.KycStatus != KycStatus.Pending) throw StateConflict();
        ChangeKyc(customer, approved ? KycStatus.Verified : KycStatus.Rejected, request.Reason, actor);
        if (approved && customer.Status == CustomerStatus.PendingVerification) ChangeStatus(customer, CustomerStatus.Active, "KYC verified", actor);
        Touch(customer); await Save(ct); return new(customer.Id, customer.KycStatus, customer.ConcurrencyVersion);
    }
    private async Task<Customer> Mine(Guid identityUserId, CancellationToken ct) => await db.Customers.SingleOrDefaultAsync(x => x.IdentityUserId == identityUserId, ct) ?? throw new CustomerRuleException("CUSTOMER_NOT_FOUND", 404, "Customer was not found.");
    private async Task<Customer> ById(Guid id, CancellationToken ct) => await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CustomerRuleException("CUSTOMER_NOT_FOUND", 404, "Customer was not found.");
    private static void ValidateProfile(string first, string last, string? phone, string? language) { Required(first, "firstName", 100); Required(last, "lastName", 100); if (phone is { Length: > 32 }) throw Validation(); if (language is { } l) Required(l, "preferredLanguage", 16); }
    private static string Required(string value, string name, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max ? throw Validation() : value.Trim();
    private static string? Clean(string? value) => value is null ? null : value.Trim().Length is 0 or > 32 ? throw Validation() : value.Trim();
    private static void RequireReason(string reason) { if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw Validation(); }
    private void SetOriginalVersion(Customer customer, Guid version) { if (version == Guid.Empty) throw Validation(); db.Entry(customer).Property(x => x.ConcurrencyVersion).OriginalValue = version; }
    private static void Touch(Customer customer) { customer.UpdatedAt = DateTimeOffset.UtcNow; customer.ConcurrencyVersion = Guid.NewGuid(); }
    private void ChangeStatus(Customer c, CustomerStatus status, string reason, Guid actor) { db.CustomerStatusHistory.Add(new CustomerStatusHistory { CustomerId = c.Id, FromStatus = c.Status, ToStatus = status, Reason = reason.Trim(), ActorIdentityUserId = actor }); c.Status = status; }
    private void ChangeKyc(Customer c, KycStatus status, string reason, Guid actor) { db.KycStatusHistory.Add(new KycStatusHistory { CustomerId = c.Id, FromStatus = c.KycStatus, ToStatus = status, Reason = reason.Trim(), ActorIdentityUserId = actor }); c.KycStatus = status; }
    private async Task Save(CancellationToken ct) { try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { throw new CustomerRuleException("CONCURRENCY_CONFLICT", 409, "The resource was changed by another request."); } }
    private static CustomerRuleException Validation() => new("VALIDATION_ERROR", 400, "The request is invalid.");
    private static CustomerRuleException Conflict(string code) => new(code, 409, "The request conflicts with existing state.");
    private static CustomerRuleException StateConflict() => new("STATE_CONFLICT", 409, "The requested state transition is invalid.");
    public static CustomerResponse Map(Customer c, bool pii) => new(c.Id, c.IdentityUserId, pii ? c.FirstName : Mask(c.FirstName) ?? string.Empty, pii ? c.LastName : Mask(c.LastName) ?? string.Empty, pii ? c.PhoneNumber : Mask(c.PhoneNumber), c.PreferredLanguage, c.Status, c.KycStatus, c.CreatedAt, c.UpdatedAt, c.ConcurrencyVersion);
    private static string? Mask(string? value) => string.IsNullOrEmpty(value) ? value : value.Length == 1 ? "*" : value[0] + new string('*', value.Length - 1);
}

public sealed class CustomerEligibilityReader(CustomersDbContext db) : ICustomerEligibilityReader
{
    public async Task<CustomerEligibility?> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var item = await db.Customers.AsNoTracking().Where(x => x.Id == customerId).Select(x => new { x.Id, x.Status, x.KycStatus }).SingleOrDefaultAsync(cancellationToken);
        return item is null ? null : new CustomerEligibility(item.Id, item.Status == CustomerStatus.Active && item.KycStatus == KycStatus.Verified);
    }
    public async Task<CustomerEligibility?> GetByIdentityUserIdAsync(Guid identityUserId, CancellationToken cancellationToken)
    {
        var item = await db.Customers.AsNoTracking().Where(x => x.IdentityUserId == identityUserId).Select(x => new { x.Id, x.Status, x.KycStatus }).SingleOrDefaultAsync(cancellationToken);
        return item is null ? null : new CustomerEligibility(item.Id, item.Status == CustomerStatus.Active && item.KycStatus == KycStatus.Verified);
    }
}

public sealed class CustomerRecipientReader(CustomersDbContext db) : ICustomerRecipientReader
{
    public async Task<MaskedCustomerName?> GetMaskedNameAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var name = await db.Customers.AsNoTracking().Where(x => x.Id == customerId && x.Status == CustomerStatus.Active && x.KycStatus == KycStatus.Verified)
            .Select(x => x.FirstName).SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? null : new MaskedCustomerName(Mask(name));
    }

    private static string Mask(string name) => name.Length == 1 ? "*" : name[0] + new string('*', name.Length - 1);
}
