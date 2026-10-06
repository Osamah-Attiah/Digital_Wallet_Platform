using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalWallet.Messaging.Contracts;

// Customer identifiers are necessary routing data. No names, contacts, notes, OTP or credentials.
public sealed record TransferCompletedV1(Guid SenderCustomerId, Guid RecipientCustomerId, long AmountMinor, string Currency);
public sealed record ReversalCompletedV1(Guid SenderCustomerId, Guid RecipientCustomerId, long AmountMinor, string Currency, Guid OriginalTransactionId);
public sealed record ReportRequestedV1(Guid ReportId);
public sealed record EventEnvelope(Guid EventId, string EventType, int EventVersion, Guid AggregateId,
    DateTimeOffset OccurredAt, string CorrelationId, Guid CausationId, JsonElement Payload);

public static class WalletEvents
{
    public const string ReportRequested = "ReportRequested";
    public const string TransferCompleted = "TransferCompleted";
    public const string ReversalCompleted = "ReversalCompleted";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static EventEnvelope Transfer(Guid id, DateTimeOffset at, string correlation, TransferCompletedV1 payload) =>
        Create(TransferCompleted, id, at, correlation, payload);
    public static EventEnvelope Reversal(Guid id, DateTimeOffset at, string correlation, ReversalCompletedV1 payload) =>
        Create(ReversalCompleted, id, at, correlation, payload);
    public static EventEnvelope Report(Guid id, DateTimeOffset at, string correlation) => Create(ReportRequested,id,at,correlation,new ReportRequestedV1(id));
    private static EventEnvelope Create<T>(string type, Guid id, DateTimeOffset at, string correlation, T payload)
    {
        // A stable identity even across transaction retries and replayed commands.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"digitalwallet:{type}:1:{id:D}"));
        var envelope = new EventEnvelope(new Guid(bytes.AsSpan(0, 16)), type, 1, id, at.ToUniversalTime(),
            string.IsNullOrEmpty(correlation) ? id.ToString("D") : correlation, id, JsonSerializer.SerializeToElement(payload, Json));
        Validate(envelope); return envelope;
    }
    public static void Validate(EventEnvelope e)
    {
        if (e.EventVersion != 1 || e.EventType is not (TransferCompleted or ReversalCompleted or ReportRequested))
            throw new UnsupportedEventException();
        var expectedId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"digitalwallet:{e.EventType}:1:{e.AggregateId:D}")).AsSpan(0, 16));
        if (e.EventId != expectedId) throw new InvalidEventException();
        if (e.EventId == Guid.Empty || e.AggregateId == Guid.Empty || e.CausationId == Guid.Empty ||
            e.OccurredAt.Offset != TimeSpan.Zero || string.IsNullOrEmpty(e.CorrelationId) || e.CorrelationId.Length > 128 ||
            e.CorrelationId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidEventException();
        if(e.EventType==ReportRequested)
        {
            try { var p=e.Payload.Deserialize<ReportRequestedV1>(Json);if(p?.ReportId!=e.AggregateId)throw new InvalidEventException(); }
            catch(JsonException){throw new InvalidEventException();}return;
        }
        Guid sender, recipient; long amount; string currency;
        try
        {
            if (e.EventType == TransferCompleted)
            {
                var p = e.Payload.Deserialize<TransferCompletedV1>(Json) ?? throw new InvalidEventException();
                (sender, recipient, amount, currency) = (p.SenderCustomerId, p.RecipientCustomerId, p.AmountMinor, p.Currency);
            }
            else
            {
                var p = e.Payload.Deserialize<ReversalCompletedV1>(Json) ?? throw new InvalidEventException();
                if (p.OriginalTransactionId == Guid.Empty) throw new InvalidEventException();
                (sender, recipient, amount, currency) = (p.SenderCustomerId, p.RecipientCustomerId, p.AmountMinor, p.Currency);
            }
        }
        catch (JsonException) { throw new InvalidEventException(); }
        if (sender == Guid.Empty || recipient == Guid.Empty || sender == recipient || amount <= 0 || currency != "SAR")
            throw new InvalidEventException();
    }
}
public sealed class UnsupportedEventException : Exception;
public sealed class InvalidEventException : Exception;
public interface IEventConsumer
{
    string Name { get; }
    Task ConsumeAsync(EventEnvelope envelope, CancellationToken ct);
}
