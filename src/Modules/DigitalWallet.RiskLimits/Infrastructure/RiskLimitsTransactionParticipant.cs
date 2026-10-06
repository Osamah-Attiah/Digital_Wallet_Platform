using System.Data;
using System.Data.Common;
using DigitalWallet.RiskLimits.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DigitalWallet.RiskLimits.Infrastructure;

// Host/composition seam only. No connection/transaction leaks into business contracts.
// The caller owns the open connection and transaction. This participant never commits them.
public sealed class RiskLimitsTransactionParticipant : IAsyncDisposable
{
    private readonly RiskLimitsDbContext _context;
    public ILimitUsage Usage { get; }
    public ILockedTransferAssessment Assessment { get; }
    private RiskLimitsTransactionParticipant(RiskLimitsDbContext context, TimeProvider clock, IOptions<RiskLimitsOptions> options)
    { _context = context; var store = new RiskLimitsStore(context, clock, options); Usage = store; Assessment = store; }
    public static async Task<RiskLimitsTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction,
        TimeProvider clock, IOptions<RiskLimitsOptions> options, CancellationToken ct = default)
    {
        if (transaction.Connection != connection || transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Risk usage requires the caller's active ReadCommitted PostgreSQL transaction.");
        var context = new RiskLimitsDbContext(new DbContextOptionsBuilder<RiskLimitsDbContext>().UseNpgsql(connection).Options);
        try { await context.Database.UseTransactionAsync(transaction, ct); return new(context, clock, options); }
        catch { await context.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _context.DisposeAsync();
}
