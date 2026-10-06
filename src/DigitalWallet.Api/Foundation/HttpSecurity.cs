using DigitalWallet.Api.Foundation.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
namespace DigitalWallet.Api.Foundation;
public static class HttpSecurity
{
    public static void AddWalletHttpSecurity(this WebApplicationBuilder b)
    {
        HttpSecurityOptions Load() => b.Configuration.GetSection("HttpSecurity").Get<HttpSecurityOptions>() ?? new();
        b.Services.AddSingleton(_ => Load());
        b.WebHost.ConfigureKestrel(k =>
        {
            var o = Load();
            k.Limits.MaxRequestBodySize = o.MaxRequestBodyBytes; k.Limits.MaxRequestHeadersTotalSize = 16384;
            k.Limits.MaxRequestHeaderCount = 40; k.Limits.MaxRequestLineSize = 4096;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(o.HeaderTimeoutSeconds);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            k.Limits.MinRequestBodyDataRate = new MinDataRate(100, TimeSpan.FromSeconds(5));
        });
        b.Services.Configure<ForwardedHeadersOptions>(f =>
        {
            var o = Load();
            f.ForwardedHeaders = o.KnownProxies.Length == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            f.ForwardLimit = 1; f.RequireHeaderSymmetry = true; f.KnownIPNetworks.Clear(); f.KnownProxies.Clear();
            foreach (var ip in o.KnownProxies) f.KnownProxies.Add(IPAddress.Parse(ip));
        });
        b.Services.AddCors(c => c.AddPolicy("AdminSpa", p =>
        {
            var o = Load();
            if (o.AdminOrigins.Length > 0) p.WithOrigins(o.AdminOrigins).WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-ID", "traceparent", "tracestate")
                .WithExposedHeaders("X-Correlation-ID", "X-Trace-ID", "Retry-After");
        }));
        b.Services.AddHsts(h => { h.MaxAge = TimeSpan.FromDays(180); h.IncludeSubDomains = false; h.Preload = false; });
        b.Services.AddHttpsRedirection(h => h.HttpsPort = 443);
        b.Services.AddRequestTimeouts(t => t.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(30), TimeoutStatusCode = 503 });
    }
}
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    public async Task InvokeAsync(HttpContext h, HttpSecurityOptions options)
    {
        h.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        h.Response.Headers["X-Content-Type-Options"] = "nosniff";
        h.Response.Headers["Referrer-Policy"] = "no-referrer";
        h.Response.Headers["X-Frame-Options"] = "DENY";
        h.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        if (h.Request.Path.StartsWithSegments("/api")) h.Response.Headers.CacheControl = "no-store";
        if (h.Request.ContentLength > options.MaxRequestBodyBytes)
        { await Results.Problem(statusCode: 413, title: "Request body is too large.").ExecuteAsync(h); return; }
        await next(h);
    }
}
