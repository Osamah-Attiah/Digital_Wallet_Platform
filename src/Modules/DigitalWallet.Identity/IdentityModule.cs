using System.Security.Claims;
using System.Text;
using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DigitalWallet.Identity.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace DigitalWallet.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdentityOptions>().Bind(configuration.GetRequiredSection(IdentityOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityOptions>, IdentityOptionsValidator>();
        services.AddDbContext<IdentityDbContext>((_, options) =>
        {
            var platform = configuration.GetRequiredSection("Platform:PostgreSql").Get<PlatformConnectionOptions>()
                ?? throw new InvalidOperationException("Platform PostgreSQL options are required.");
            options.UseNpgsql(platform.ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema));
        });
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<IdentityDbContext>()
            .AddDefaultTokenProviders();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            var jwt = configuration.GetRequiredSection("Identity:Jwt").Get<JwtOptions>() ?? new JwtOptions();
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async validation =>
                {
                    var principal = validation.Principal!;
                    var stamp = principal.FindFirstValue("token_version");
                    if (!Guid.TryParse(principal.FindFirstValue("sub"), out var userId) ||
                        !Guid.TryParse(principal.FindFirstValue("sid"), out var familyId) || string.IsNullOrEmpty(stamp) || !int.TryParse(principal.FindFirstValue("authorization_version"), out var authorizationVersion))
                    {
                        validation.Fail("Invalid session.");
                        return;
                    }
                    var db = validation.HttpContext.RequestServices.GetRequiredService<IdentityDbContext>();
                    var now = DateTimeOffset.UtcNow;
                    var active = await db.RefreshTokens.AsNoTracking().AnyAsync(token =>
                        token.UserId == userId && token.FamilyId == familyId &&
                        token.RevokedAt == null && token.UsedAt == null && token.ExpiresAt > now &&
                        token.SecurityStamp == stamp && token.User.SecurityStamp == stamp &&
                        token.AuthorizationVersion == authorizationVersion && token.User.AuthorizationVersion == authorizationVersion &&
                        token.User.EmailConfirmed &&
                        (!token.User.LockoutEnabled || token.User.LockoutEnd == null || token.User.LockoutEnd <= now) &&
                        token.Device.RevokedAt == null, validation.HttpContext.RequestAborted);
                    if (!active) { DigitalWallet.SharedKernel.Observability.WalletTelemetry.Security(DigitalWallet.SharedKernel.Observability.SecurityEvent.SessionRejected, DigitalWallet.SharedKernel.Observability.TelemetryModule.Identity); validation.Fail("Session is no longer active."); }
                    else
                    {
                        var live = validation.HttpContext.RequestServices.GetRequiredService<AuthorizationReadContext>();
                        live.Actor = userId; live.ValidatedVersion = authorizationVersion;
                    }
                }
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.NameIdentifier, RoleClaimType = ClaimTypes.Role
            };
        });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, DeviceOwnershipAuthorizationHandler>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<EffectivePermissions>();
        services.AddScoped<AuthorizationReadContext>();
        services.AddHttpContextAccessor();
        services.AddScoped<IStaffAccess, StaffAccess>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IOtpChallengeService>(provider => (IdentityService)provider.GetRequiredService<IIdentityService>());
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddSingleton<IOtpCodeGenerator, OtpCodeGenerator>();
        services.AddSingleton<IOtpSender, NullOtpSender>();
        return services;
    }

    public static async Task InitializeIdentityModuleAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
        await IdentitySeed.EnsureAsync(scope.ServiceProvider, cancellationToken);
    }

    private sealed class PlatformConnectionOptions { public string ConnectionString { get; init; } = string.Empty; }
}
