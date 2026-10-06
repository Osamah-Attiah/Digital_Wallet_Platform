using DigitalWallet.Identity.Services;
using DigitalWallet.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
namespace DigitalWallet.Api.Hosting;

// An isolated synthetic demonstration uses a private, ephemeral operator mailbox.
// No public OTP endpoint, external message delivery or financial fixture is registered.
public static class SyntheticDemoOtp
{
    public static void AddSyntheticDemoOtp(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("SyntheticDemo:Enabled")) return;
        if (configuration["SyntheticDemo:DataClassification"] != "synthetic-only")
            throw new InvalidOperationException("Synthetic demo requires an explicit data classification.");
        services.RemoveAll<IOtpSender>();
        services.AddSingleton<IOtpSender, SyntheticDemoOtpSender>();
    }
}
public sealed class SyntheticDemoOtpSender(IConfiguration configuration) : IOtpSender
{
    public async Task SendAsync(Guid challengeId, string target, string code, OtpPurpose purpose, CancellationToken cancellationToken)
    {
        if (!target.EndsWith("@demo.invalid", StringComparison.Ordinal) || target.Any(char.IsControl))
            throw new InvalidOperationException("Only synthetic demo recipients are allowed.");
        var directory = "/var/demo-otp";
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Private ephemeral demo mailbox is required.");
        var lifetime = configuration.GetValue("Identity:Otp:LifetimeMinutes", 10);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-lifetime)) File.Delete(file);
        if (Directory.EnumerateFiles(directory, "*.json").Take(256).Count() >= 256)
            throw new InvalidOperationException("Synthetic mailbox capacity reached.");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(Path.Combine(directory, challengeId.ToString("D") + ".json"), options);
        await JsonSerializer.SerializeAsync(stream, new { code, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(lifetime) }, cancellationToken: cancellationToken);
    }
}
