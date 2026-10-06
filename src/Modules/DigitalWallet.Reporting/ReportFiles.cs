using DigitalWallet.SharedKernel.Observability;
using System.Text;
using System.Globalization;
using DigitalWallet.Reporting.Contracts;
namespace DigitalWallet.Reporting;

public sealed class LocalReportFileStore(ReportOptions options) : IReportFileStore
{
    private string PathFor(Guid id) => Path.Combine(Path.GetFullPath(options.StoragePath), id.ToString("N") + ".csv");
    public Task<byte[]?> ReadAsync(Guid id, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportStorage, () => ReadAsyncCore(id, ct));
    private async Task<byte[]?> ReadAsyncCore(Guid id, CancellationToken ct) { var path = PathFor(id); return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null; }
    public Task<byte[]> PutIfAbsentAsync(Guid id, byte[] content, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportStorage, () => PutIfAbsentAsyncCore(id, content, ct));
    private async Task<byte[]> PutIfAbsentAsyncCore(Guid id, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetFullPath(options.StoragePath)); var path = PathFor(id); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            // Atomic, exclusive publication; all deliveries share this one logical object key.
            try { File.Move(temporary, path, overwrite: false); } catch (IOException) when (File.Exists(path)) { }
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task DeleteAsync(Guid id, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportStorage, () => { ct.ThrowIfCancellationRequested(); File.Delete(PathFor(id)); return Task.CompletedTask; });
}
public static class SafeCsv
{
    public static string Cell(object? input)
    {
        var value = input is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : input?.ToString() ?? "";
        value = value.Replace("\r", " ").Replace("\n", " ");
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' || value.Length > 0 && value[0] is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static byte[] Encode(string[] columns, IEnumerable<object?[]> rows)
    {
        var text = string.Join(",", columns.Select(Cell)) + "\r\n" + string.Concat(rows.Select(r => string.Join(",", r.Select(Cell)) + "\r\n"));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }
    public static int RowCount(byte[] bytes) => Encoding.UTF8.GetString(bytes).Count(c => c == '\n') - 1;
}
