using System.Data.Common;
using DigitalWallet.Beneficiaries.Contracts;
using DigitalWallet.Beneficiaries.Domain;
using DigitalWallet.SharedKernel.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DigitalWallet.Beneficiaries.Infrastructure;

public sealed class BeneficiariesTransactionParticipant : ITransferBeneficiaryReader, IAsyncDisposable
{
    private readonly BeneficiariesDbContext _db;
    private BeneficiariesTransactionParticipant(BeneficiariesDbContext db) => _db = db;
    public static async Task<BeneficiariesTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new BeneficiariesDbContext(new DbContextOptionsBuilder<BeneficiariesDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    private Task<T> Guard<T>(Func<Task<T>> action, CancellationToken ct) =>
        ParticipantSavepoint.RunAsync(_db.Database.CurrentTransaction!.GetDbTransaction(), action, ct);
    public Task<TransferBeneficiary?> ReadAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct) => Guard(async () =>
        await _db.Beneficiaries.AsNoTracking().Where(x => x.Id == beneficiaryId && x.OwnerCustomerId == ownerCustomerId)
            .Select(x => new TransferBeneficiary(x.Id, x.TargetWalletId, x.Status == BeneficiaryStatus.Active, x.MaskedRecipientName)).SingleOrDefaultAsync(ct), ct);
    public Task<TransferBeneficiary?> GetAsync(Guid ownerCustomerId, Guid beneficiaryId, CancellationToken ct) => Guard(async () =>
    {
        var rows = await _db.Beneficiaries.FromSqlInterpolated($"SELECT * FROM beneficiaries.beneficiaries WHERE \"Id\" = {beneficiaryId} AND \"OwnerCustomerId\" = {ownerCustomerId} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        var b = rows.SingleOrDefault();
        return b is null ? null : new TransferBeneficiary(b.Id, b.TargetWalletId, b.Status == BeneficiaryStatus.Active, b.MaskedRecipientName);
    }, ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
