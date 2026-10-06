using DigitalWallet.SharedKernel.Observability;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Domain;

namespace DigitalWallet.Ledger.Application;

public interface ILedgerStore
{
    Task<PostedJournal> PostAsync(PostJournal request, string fingerprint, CancellationToken ct);
    Task<PostJournal?> ReadOriginalAsync(Guid id, CancellationToken ct);
    Task<ReconciliationReport> ReconcileAsync(IReadOnlyList<WalletLedgerReference> wallets, CancellationToken ct);
}

public sealed class LedgerPostingService(ILedgerStore store) : ILedgerPosting
{
    public Task<PostedJournal> PostAsync(PostJournal request, CancellationToken ct = default) => WalletTelemetry.RunAsync(TelemetryModule.Ledger, OperationName.Post, () => PostAsyncCore(request, ct));
    private Task<PostedJournal> PostAsyncCore(PostJournal request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceType) || request.SourceType.Length > 64 ||
            string.IsNullOrWhiteSpace(request.SourceId) || request.SourceId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500 ||
            !LedgerRules.Currencies.Contains(request.Currency) || request.Entries is null ||
            request.Entries.Count > 1000 || request.Entries.Any(x => x.AccountId == Guid.Empty))
            throw new LedgerException("VALIDATION_ERROR", "The journal request is invalid.");
        // Copy caller-owned collections before awaiting. Line order has no financial meaning.
        var entries = request.Entries.OrderBy(x => x.AccountId).ThenBy(x => x.Side).ThenBy(x => x.AmountMinor).ToArray();
        if (!LedgerRules.IsBalanced(entries.Select(x => ((EntrySide)x.Side, x.AmountMinor))))
            throw new LedgerException("JOURNAL_UNBALANCED", "A journal requires positive, balanced debit and credit lines.");
        var canonical = request with { Entries = entries };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
        return store.PostAsync(canonical, hash, ct);
    }

    public async Task<PostedJournal> ReverseAsync(Guid originalId, string sourceType, string sourceId,
        string reason, Guid actorId, CancellationToken ct = default)
    {
        var original = await store.ReadOriginalAsync(originalId, ct)
            ?? throw new LedgerException("JOURNAL_NOT_FOUND", "The journal was not found.");
        return await PostAsync(new(sourceType, sourceId, original.Currency,
            original.Entries.Select(x => x with { Side = x.Side == PostingSide.Debit ? PostingSide.Credit : PostingSide.Debit }).ToArray(),
            reason, actorId, originalId), ct);
    }
}

public sealed class LedgerReconciliationService(ILedgerStore store, IWalletLedgerInventory wallets) : ILedgerReconciliation
{
    public async Task<ReconciliationReport> RunAsync(CancellationToken ct = default) =>
        await store.ReconcileAsync(await wallets.ListAsync(ct), ct);
}
