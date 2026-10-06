using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Customers.Services;

public sealed class AdminCustomerReader(CustomersDbContext db, TimeProvider clock) : IAdminCustomerReader
{
    public async Task<CustomerSearchPage> SearchAsync(CustomerSearch s, Guid actor, CancellationToken ct)
    {
        var size = Keyset.Size(s.PageSize);
        if (s.Status != null && (!Enum.TryParse<CustomerStatus>(s.Status, out var status) || !Enum.IsDefined(status)) ||
            s.KycStatus != null && (!Enum.TryParse<KycStatus>(s.KycStatus, out var kyc) || !Enum.IsDefined(kyc))) throw new AdminException("VALIDATION_ERROR");
        var hash = Keyset.Fingerprint(new { actor, s.Status, s.KycStatus, s.Id });
        var c = Keyset.Decode(s.Cursor, hash, 36600); var from = c?.From ?? DateTimeOffset.UnixEpoch; var to = c?.To ?? clock.GetUtcNow().AddSeconds(1);
        var q = db.Customers.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to);
        if (s.Id is { } id) q = q.Where(x => x.Id == id);
        if (s.Status != null) { var v = Enum.Parse<CustomerStatus>(s.Status); q = q.Where(x => x.Status == v); }
        if (s.KycStatus != null) { var v = Enum.Parse<KycStatus>(s.KycStatus); q = q.Where(x => x.KycStatus == v); }
        if (c != null) q = q.Where(x => x.CreatedAt < c.At || x.CreatedAt == c.At && x.Id.CompareTo(c.Id) < 0);
        var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).Select(x => new Customer { Id = x.Id, FirstName = x.FirstName, LastName = x.LastName, PhoneNumber = x.PhoneNumber, Status = x.Status, KycStatus = x.KycStatus, CreatedAt = x.CreatedAt, ConcurrencyVersion = x.ConcurrencyVersion }).ToListAsync(ct);
        var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows.Select(x => Map(x, false)).ToArray(), more ? Keyset.Encode(rows[^1].CreatedAt, rows[^1].Id, from, to, hash) : null);
    }
    public async Task<AdminCustomer?> GetAsync(Guid id, bool pii, CancellationToken ct)
    { var c = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); return c == null ? null : Map(c, pii); }
    public async Task<IReadOnlyList<AdminCustomer>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, bool pii, CancellationToken ct) =>
        (await db.Customers.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(limit).ToListAsync(ct)).Select(x => Map(x, pii)).ToArray();
    public Task<long> CountAsync(CancellationToken ct) => db.Customers.LongCountAsync(ct);
    private static AdminCustomer Map(Customer c, bool pii)
    { var r = CustomerService.Map(c, pii); return new(r.Id, r.FirstName, r.LastName, r.PhoneNumber, r.Status.ToString(), r.KycStatus.ToString(), r.CreatedAt, r.ConcurrencyVersion); }
}
