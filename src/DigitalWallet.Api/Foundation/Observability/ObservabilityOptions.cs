namespace DigitalWallet.Api.Foundation.Observability;
public sealed class ObservabilityOptions
{
    public bool ExportEnabled { get; init; }
    public bool AllowLocalContainerEndpoint { get; init; }
    public string Endpoint { get; init; } = "";
    public double SampleRatio { get; init; } = 1;
    public int QueueCapacity { get; init; } = 2048;
    public int ExportTimeoutMilliseconds { get; init; } = 500;
    public int MetricIntervalMilliseconds { get; init; } = 10000;
    public bool ReconciliationEnabled { get; init; } = true;
    public int ReconciliationIntervalSeconds { get; init; } = 300;
    public int ReconciliationTimeoutSeconds { get; init; } = 120;
    public void Validate(bool production)
    {
        if (!double.IsFinite(SampleRatio) || SampleRatio is < 0 or > 1 || QueueCapacity is < 16 or > 8192 ||
            ExportTimeoutMilliseconds is < 100 or > 2000 || MetricIntervalMilliseconds is < 1000 or > 60000 ||
            ReconciliationIntervalSeconds is < 30 or > 3600 || ReconciliationTimeoutSeconds is < 5 or > 300)
            throw new InvalidOperationException("Invalid observability limits.");
        if (production && AllowLocalContainerEndpoint) throw new InvalidOperationException("Local collector transport is forbidden in Production.");
        if (ExportEnabled && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" ||
            production && uri.Scheme != "https" || !production && uri.Scheme == "http" && !uri.IsLoopback && !(AllowLocalContainerEndpoint && uri.Host == "collector")))
            throw new InvalidOperationException("Observability endpoint must be a trusted TLS collector base URI (loopback HTTP is allowed locally).");
    }
}
