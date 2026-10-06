using System.ComponentModel.DataAnnotations;

namespace DigitalWallet.Api.Foundation.Configuration;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    [Required]
    public string BusinessTimeZone { get; init; } = "Asia/Riyadh";

    [Required]
    public PostgreSqlOptions PostgreSql { get; init; } = new();
}

public sealed class PostgreSqlOptions
{
    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    [RegularExpression("^[a-z][a-z0-9_]*$", ErrorMessage = "Schema must use lowercase letters, digits, and underscores only.")]
    public string DefaultSchema { get; init; } = "platform";
}
