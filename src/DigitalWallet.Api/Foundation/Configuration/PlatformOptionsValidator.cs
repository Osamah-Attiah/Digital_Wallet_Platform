using System.Data.Common;
using Microsoft.Extensions.Options;

namespace DigitalWallet.Api.Foundation.Configuration;

public sealed class PlatformOptionsValidator : IValidateOptions<PlatformOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BusinessTimeZone))
        {
            failures.Add("Platform:BusinessTimeZone is required.");
        }
        else
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                failures.Add("Platform:BusinessTimeZone must identify a valid system time zone.");
            }
            catch (InvalidTimeZoneException)
            {
                failures.Add("Platform:BusinessTimeZone is invalid on this host.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.PostgreSql.ConnectionString))
        {
            failures.Add("Platform:PostgreSql:ConnectionString is required.");
        }
        else
        {
            try
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = options.PostgreSql.ConnectionString };
                if (!builder.ContainsKey("Host") || !builder.ContainsKey("Database"))
                {
                    failures.Add("Platform:PostgreSql:ConnectionString must include Host and Database.");
                }
            }
            catch (ArgumentException)
            {
                failures.Add("Platform:PostgreSql:ConnectionString is invalid.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
