using System.Text.Json;
using DigitalWallet.Customers.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Notifications;

public sealed record NotificationReceipt(Guid Id, Guid AggregateId, string Kind, string Direction, long AmountMinor,
    string Currency, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{ public bool IsRead => ReadAt != null; }
public sealed record NotificationPage(IReadOnlyList<NotificationReceipt> Items, string? NextCursor);
public sealed record PreferenceRequest(bool InAppEnabled, bool TransfersEnabled, bool ReversalsEnabled);
public sealed class NotificationException(string code) : Exception { public string Code { get; } = code; }

public sealed class NotificationService(NotificationsDbContext db, ICustomerEligibilityReader customers, TimeProvider clock)
{
    private async Task<Guid> Owner(Guid identity, CancellationToken ct) =>
        (await customers.GetByIdentityUserIdAsync(identity, ct))?.CustomerId ?? throw new NotificationException("NOTIFICATION_NOT_FOUND");
    public async Task<NotificationReceipt> GetAsync(Guid identity, Guid id, CancellationToken ct)
    {
        var owner = await Owner(identity, ct);
        return Receipt(await db.Notifications.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == owner && x.Id == id, ct)
            ?? throw new NotificationException("NOTIFICATION_NOT_FOUND"));
    }
    public async Task<NotificationPage> ListAsync(Guid identity, string? cursor, int? pageSize, CancellationToken ct)
    {
        var owner = await Owner(identity, ct); var size = pageSize ?? 20;
        if (size is < 1 or > 100) throw new NotificationException("VALIDATION_ERROR");
        var query = db.Notifications.AsNoTracking().Where(x => x.CustomerId == owner);
        if (cursor is not null)
        {
            Cursor parsed;
            try
            {
                if (cursor.Length > 512) throw new FormatException();
                parsed = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException();
                if (parsed.Owner != owner || parsed.Id == Guid.Empty || parsed.At.Offset != TimeSpan.Zero) throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            { throw new NotificationException("INVALID_CURSOR"); }
            query = query.Where(x => x.CreatedAt < parsed.At || x.CreatedAt == parsed.At && x.Id.CompareTo(parsed.Id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).Select(x => new NotificationReceipt(x.Id, x.AggregateId, x.Kind, x.Direction, x.AmountMinor, x.Currency, x.CreatedAt, x.ReadAt)).ToListAsync(ct);
        var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows.ToArray(), more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(owner, rows[^1].CreatedAt, rows[^1].Id))) : null);
    }
    public async Task<NotificationReceipt> MarkReadAsync(Guid identity, Guid id, CancellationToken ct)
    {
        var owner = await Owner(identity, ct);
        await db.Notifications.Where(x => x.Id == id && x.CustomerId == owner && x.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, clock.GetUtcNow()), ct);
        return await GetAsync(identity, id, ct);
    }
    public async Task<PreferenceRequest> PreferencesAsync(Guid identity, CancellationToken ct)
    {
        var owner = await Owner(identity, ct); var p = await db.Preferences.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == owner, ct);
        return p is null ? new(true, true, true) : new(p.InAppEnabled, p.TransfersEnabled, p.ReversalsEnabled);
    }
    public async Task<PreferenceRequest> SetPreferencesAsync(Guid identity, PreferenceRequest p, CancellationToken ct)
    {
        var owner = await Owner(identity, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.preferences ("CustomerId", "InAppEnabled", "TransfersEnabled", "ReversalsEnabled")
            VALUES ({owner}, {p.InAppEnabled}, {p.TransfersEnabled}, {p.ReversalsEnabled})
            ON CONFLICT ("CustomerId") DO UPDATE SET "InAppEnabled" = EXCLUDED."InAppEnabled",
                "TransfersEnabled" = EXCLUDED."TransfersEnabled", "ReversalsEnabled" = EXCLUDED."ReversalsEnabled"
            """, ct);
        return p;
    }
    private static NotificationReceipt Receipt(InAppNotification x) => new(x.Id, x.AggregateId, x.Kind, x.Direction, x.AmountMinor, x.Currency, x.CreatedAt, x.ReadAt);
    private sealed record Cursor(Guid Owner, DateTimeOffset At, Guid Id);
}
