using System.Security.Cryptography;
using System.Text.Json;
namespace DigitalWallet.SharedKernel.Administration;

public sealed class AdminException(string code, int status = 400) : Exception("The administrative request could not be completed.")
{ public string Code { get; } = code; public int Status { get; } = status; }
public sealed record KeysetCursor(DateTimeOffset At, Guid Id, DateTimeOffset From, DateTimeOffset To, string Filter);
public static class Keyset
{
    public static string Fingerprint<T>(T filters) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(filters)));
    public static int Size(int? size) => size is null ? 20 : size is >= 1 and <= 100 ? size.Value : throw new AdminException("VALIDATION_ERROR");
    public static KeysetCursor? Decode(string? raw, string filter, int days)
    {
        if (raw is null) return null;
        try
        {
            if (raw.Length > 2048) throw new FormatException();
            var c = JsonSerializer.Deserialize<KeysetCursor>(Convert.FromBase64String(raw)) ?? throw new FormatException();
            if (c.Filter != filter || c.Id == Guid.Empty || c.At.Offset != TimeSpan.Zero || c.At < c.From || c.At >= c.To) throw new FormatException();
            Window(c.From, c.To, days); return c;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or AdminException) { throw new AdminException("INVALID_CURSOR"); }
    }
    public static void Window(DateTimeOffset from, DateTimeOffset to, int days)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to) throw new AdminException("VALIDATION_ERROR");
        if (to - from > TimeSpan.FromDays(days)) throw new AdminException("DATE_RANGE_TOO_LARGE");
    }
    public static string Encode(DateTimeOffset at, Guid id, DateTimeOffset from, DateTimeOffset to, string filter) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new KeysetCursor(at, id, from, to, filter)));
}
