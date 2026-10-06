using System.Data;
using System.Data.Common;

namespace DigitalWallet.SharedKernel.Transactions;

// Infrastructure utility only; business contracts never expose connections or transactions.
public static class ParticipantSavepoint
{
    public static void Validate(DbConnection connection, DbTransaction transaction)
    {
        if (transaction.Connection != connection || transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("An active shared ReadCommitted transaction is required.");
    }
    public static async Task<T> RunAsync<T>(DbTransaction transaction, Func<Task<T>> action, CancellationToken ct)
    {
        var name = "participant_" + Guid.NewGuid().ToString("N");
        await transaction.SaveAsync(name, ct);
        try
        {
            var result = await action();
            await transaction.ReleaseAsync(name, ct);
            return result;
        }
        catch
        {
            try { await transaction.RollbackAsync(name, CancellationToken.None); }
            catch { /* Preserve original failure; the coordinator abandons an unusable transaction. */ }
            throw;
        }
    }
}
