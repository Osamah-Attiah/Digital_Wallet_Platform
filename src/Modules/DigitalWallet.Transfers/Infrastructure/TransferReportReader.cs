using DigitalWallet.Transfers.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Transfers.Infrastructure;

public sealed class TransferReportReader(TransfersDbContext db) : ITransferReportReader
{
    public async Task<IReadOnlyList<ExportTransfer>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct) =>
        await db.Transfers.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(limit)
            .Select(x => new ExportTransfer(x.Id, x.PublicReference, x.Status.ToString(), x.AmountMinor, x.FeeMinor, x.Currency, x.MaskedSenderName, x.MaskedRecipientName, x.CreatedAt)).ToListAsync(ct);
}
