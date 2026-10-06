namespace DigitalWallet.Transfers.Contracts;

public sealed record ExportTransfer(Guid Id, string Reference, string Status, long AmountMinor, long FeeMinor, string Currency,
    string MaskedSenderName, string MaskedRecipientName, DateTimeOffset CreatedAt);
public interface ITransferReportReader
{ Task<IReadOnlyList<ExportTransfer>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct); }
