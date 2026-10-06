using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace DigitalWallet.Identity;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";
    public JwtOptions Jwt { get; init; } = new();
    public OtpOptions Otp { get; init; } = new();
    public string TokenHashKey { get; init; } = string.Empty;
}

public sealed class JwtOptions { public string Issuer { get; init; } = string.Empty; public string Audience { get; init; } = string.Empty; public string SigningKey { get; init; } = string.Empty; public int AccessTokenLifetimeMinutes { get; init; } = 10; public int RefreshTokenLifetimeDays { get; init; } = 30; }
public sealed class OtpOptions { public string HashKey { get; init; } = string.Empty; public int LifetimeMinutes { get; init; } = 10; public int MaxAttempts { get; init; } = 5; public int ResendCooldownSeconds { get; init; } = 60; public int MaxResends { get; init; } = 3; }

public sealed class IdentityOptionsValidator(IHostEnvironment environment) : IValidateOptions<IdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Jwt.Issuer)) failures.Add("Identity:Jwt:Issuer is required.");
        if (string.IsNullOrWhiteSpace(options.Jwt.Audience)) failures.Add("Identity:Jwt:Audience is required.");
        if (options.Jwt.SigningKey.Length < 32) failures.Add("Identity:Jwt:SigningKey must be at least 32 characters.");
        if (options.Jwt.AccessTokenLifetimeMinutes is < 5 or > 10) failures.Add("Identity:Jwt:AccessTokenLifetimeMinutes must be between 5 and 10.");
        if (options.Jwt.RefreshTokenLifetimeDays is < 1 or > 90) failures.Add("Identity:Jwt:RefreshTokenLifetimeDays must be between 1 and 90.");
        if (options.Otp.HashKey.Length < 32) failures.Add("Identity:Otp:HashKey must be at least 32 characters.");
        if (options.TokenHashKey.Length < 32) failures.Add("Identity:TokenHashKey must be at least 32 characters.");
        if (options.Otp.LifetimeMinutes is < 1 or > 15) failures.Add("Identity:Otp:LifetimeMinutes must be between 1 and 15.");
        if (options.Otp.MaxAttempts is < 3 or > 10) failures.Add("Identity:Otp:MaxAttempts must be between 3 and 10.");
        if (options.Otp.ResendCooldownSeconds is < 30 or > 300) failures.Add("Identity:Otp:ResendCooldownSeconds must be between 30 and 300.");
        if (options.Otp.MaxResends is < 1 or > 5) failures.Add("Identity:Otp:MaxResends must be between 1 and 5.");
        if (environment.IsProduction() && (options.Jwt.SigningKey.StartsWith("development-only", StringComparison.Ordinal) || options.Otp.HashKey.StartsWith("development-only", StringComparison.Ordinal) || options.TokenHashKey.StartsWith("development-only", StringComparison.Ordinal))) failures.Add("Identity secrets must be supplied through a secret store in production.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
