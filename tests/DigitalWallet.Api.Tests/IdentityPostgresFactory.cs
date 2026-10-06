using DigitalWallet.Audit;
using DigitalWallet.Administration;
using DigitalWallet.Reporting;
using DigitalWallet.Transactions;
using DigitalWallet.Messaging;
using DigitalWallet.Notifications;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Infrastructure;
using System.Collections.Concurrent;
using DigitalWallet.Transfers;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Infrastructure;
using DigitalWallet.Identity;
using DigitalWallet.Customers;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Wallets;
using DigitalWallet.Wallets.Persistence;
using DigitalWallet.Ledger;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.Beneficiaries;
using DigitalWallet.RiskLimits;
using DigitalWallet.RiskLimits.Infrastructure;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Identity.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class IdentityDatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminConnection;
    private readonly string _database = "identity_tests_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable("DIGITAL_WALLET_TEST_CONNECTION");
        try
        {
            if (string.IsNullOrWhiteSpace(supplied))
            {
                _container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();
                await _container.StartAsync();
                supplied = _container.GetConnectionString();
            }
            // Even the optional external server gets a unique disposable database.
            // Never truncate or migrate the database supplied by the developer.
            await using var admin = new NpgsqlConnection(supplied);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
            await command.ExecuteNonQueryAsync();
            _adminConnection = supplied;
            // Leave room below PostgreSQL's default 100 connections for migration/admin connections.
            // Concurrent operations above this pool size queue, while dozens still overlap on real locks.
            ConnectionString = new NpgsqlConnectionStringBuilder(supplied) { Database = _database, MaxPoolSize = 64 }.ConnectionString;
            await using var factory = new IdentityPostgresFactory(ConnectionString);
            await factory.Services.InitializeIdentityModuleAsync();
            await factory.Services.InitializeCustomersModuleAsync();
            await factory.Services.InitializeLedgerModuleAsync();
            await factory.Services.InitializeWalletsModuleAsync();
            await factory.Services.InitializeBeneficiariesModuleAsync();
            await factory.Services.InitializeRiskLimitsModuleAsync();
            await factory.Services.InitializeTransfersModuleAsync();
            await factory.Services.InitializeTransactionsModuleAsync();
            await factory.Services.InitializeMessagingModuleAsync();
            await factory.Services.InitializeNotificationsModuleAsync();
            await factory.Services.InitializeAuditModuleAsync();
            await factory.Services.InitializeAdministrationModuleAsync();
            await factory.Services.InitializeReportingModuleAsync();
        }
        catch (Exception exception)
        {
            await DisposeAsync();
            throw new InvalidOperationException(
                "PostgreSQL integration tests cannot start. Run a Docker-compatible daemon for Testcontainers, " +
                "or set DIGITAL_WALLET_TEST_CONNECTION to a PostgreSQL server with CREATEDB permission. Tests are never skipped.", exception);
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_adminConnection is not null)
            {
                await using var admin = new NpgsqlConnection(_adminConnection);
                await admin.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
                await command.ExecuteNonQueryAsync();
                _adminConnection = null;
            }
        }
        finally
        {
            if (_container is not null) { await _container.DisposeAsync(); _container = null; }
        }
    }
}

public sealed class IdentityPostgresFactory(string connectionString, IReadOnlyDictionary<string, string?>? configurationOverrides = null) : WebApplicationFactory<Program>
{
    public Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? CommandObserver { get; set; }
    public Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor? ConnectionObserver { get; set; }
    private int _failOtpCreateRequests;
    private int _failAfterOtpCreateResponses;
    public IReadOnlyDictionary<string, string?>? CachingConfiguration { get; set; }
    public DigitalWallet.Reporting.Contracts.IReportCheckpoint? ReportCheckpointOverride {get;set;}
    public TransferFaults TransferFaults { get; } = new();
    public ReversalFaults ReversalFaults { get; } = new();
    public MessagingFaults MessagingFaults { get; } = new();
    public CapturingOtpSender Otp { get; } = new();
    public MutableTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public int FailOtpCreateRequests { get => Volatile.Read(ref _failOtpCreateRequests); set => Volatile.Write(ref _failOtpCreateRequests, value); }
    public int FailAfterOtpCreateResponses { get => Volatile.Read(ref _failAfterOtpCreateResponses); set => Volatile.Write(ref _failAfterOtpCreateResponses, value); }
    public bool TakeOtpCreateFailure() => Interlocked.CompareExchange(ref _failOtpCreateRequests, 0, 1) == 1;
    public bool TakeAfterOtpCreateFailure() => Interlocked.CompareExchange(ref _failAfterOtpCreateResponses, 0, 1) == 1;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
            ["Platform:PostgreSql:ConnectionString"] = connectionString,
            ["Messaging:WorkersEnabled"] = "false",
            ["Messaging:RabbitUri"] = "amqp://guest:guest@localhost:5672/",
            ["Identity:Jwt:SigningKey"] = "testing-only-signing-key-never-use-in-production-32-bytes",
            ["Identity:Otp:HashKey"] = "testing-only-otp-hash-key-never-use-in-production",
            ["Identity:TokenHashKey"] = "testing-only-token-hash-key-never-use-in-production",
            ["AllowedHosts"] = "*",
            ["Observability:ReconciliationEnabled"] = "false",
            ["Reporting:WorkersEnabled"] = "false",
            ["Reporting:StoragePath"] = "/private/tmp/wallet-reports-tests-" + new NpgsqlConnectionStringBuilder(connectionString).Database,
            ["Logging:LogLevel:Default"] = "Warning"
            };
            if (configurationOverrides is not null)
                foreach (var (key, value) in configurationOverrides) values[key] = value;
            if (CachingConfiguration is not null) foreach (var (key, value) in CachingConfiguration) values[key] = value;
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(x => x.ImplementationType == typeof(DigitalWallet.Wallets.Services.WalletProvisioningWorker)).ToArray())
                services.Remove(worker);
            services.RemoveAll<DbContextOptions<CustomersDbContext>>();
            services.RemoveAll<DbContextOptions<WalletsDbContext>>();
            services.RemoveAll<DbContextOptions<BeneficiariesDbContext>>();
            services.AddDbContext<CustomersDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", CustomersDbContext.Schema)));
            services.AddDbContext<WalletsDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", WalletsDbContext.Schema)));
            services.AddDbContext<BeneficiariesDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", BeneficiariesDbContext.Schema)));
            services.RemoveAll<DbContextOptions<RiskLimitsDbContext>>();
            services.AddDbContext<RiskLimitsDbContext>(options => options.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", RiskLimitsDbContext.Schema)));
            services.RemoveAll<DbContextOptions<TransfersDbContext>>();
            services.AddDbContext<TransfersDbContext>(options => options.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", TransfersDbContext.Schema)));
            services.RemoveAll<DbContextOptions<TransactionsDbContext>>();
            services.AddDbContext<TransactionsDbContext>(options => options.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", TransactionsDbContext.Schema)));
            services.RemoveAll<DigitalWallet.Reporting.Contracts.IReportCheckpoint>();
            services.AddSingleton<DigitalWallet.Reporting.Contracts.IReportCheckpoint>(new DelegatingReportCheckpoint(this));
            services.RemoveAll<IReversalCheckpoint>();
            services.RemoveAll<IMessagingCheckpoint>();
            services.RemoveAll<INotificationCheckpoint>();
            services.AddSingleton<IMessagingCheckpoint>(MessagingFaults);
            services.AddSingleton<INotificationCheckpoint>(MessagingFaults);
            services.AddSingleton<IReversalCheckpoint>(ReversalFaults);
            services.RemoveAll<ITransferCheckpoint>();
            services.AddSingleton<ITransferCheckpoint>(TransferFaults);
            services.RemoveAll<IOtpSender>();
            services.AddSingleton<IOtpSender>(Otp);
            services.RemoveAll<IOtpChallengeService>();
            services.AddScoped<IOtpChallengeService>(provider => new FaultInjectingOtpChallengeService(
                (IdentityService)provider.GetRequiredService<IIdentityService>(), this));
            if (CommandObserver is { } observer)
            {
                services.ConfigureDbContext<IdentityDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<CustomersDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<WalletsDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<BeneficiariesDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<RiskLimitsDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<TransfersDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<TransactionsDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<OutboxDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<NotificationsDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<ReportingDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<AuditDbContext>(o => o.AddInterceptors(observer));
                services.ConfigureDbContext<AdministrationDbContext>(o => o.AddInterceptors(observer));
            }
            if (ConnectionObserver is { } connectionObserver)
            {
                services.ConfigureDbContext<IdentityDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<CustomersDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<WalletsDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<BeneficiariesDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<RiskLimitsDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<TransfersDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<TransactionsDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<OutboxDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<NotificationsDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<ReportingDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<AuditDbContext>(o => o.AddInterceptors(connectionObserver));
                services.ConfigureDbContext<AdministrationDbContext>(o => o.AddInterceptors(connectionObserver));
            }
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public async Task ResetAsync()
    {
        await using (var clean = Services.CreateAsyncScope())
        {
            await clean.ServiceProvider.GetRequiredService<ReportingDbContext>().Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE reporting.inbox DISABLE TRIGGER USER; TRUNCATE reporting.inbox, reporting.jobs; ALTER TABLE reporting.inbox ENABLE TRIGGER USER; COMMIT;");
            await clean.ServiceProvider.GetRequiredService<AuditDbContext>().Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE audit.logs DISABLE TRIGGER USER; TRUNCATE audit.logs; ALTER TABLE audit.logs ENABLE TRIGGER USER; COMMIT;");
        }
        TransferFaults.Reset();
        ReversalFaults.Reset();
        MessagingFaults.Reset();
        Time.SetUtcNow(DateTimeOffset.UtcNow);
        FailOtpCreateRequests = 0;
        FailAfterOtpCreateResponses = 0;
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var wallets = scope.ServiceProvider.GetRequiredService<WalletsDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var beneficiaries = scope.ServiceProvider.GetRequiredService<BeneficiariesDbContext>();
        await scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE messaging.outbox;");
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE notifications.inbox, notifications.notifications, notifications.preferences;");
        // Destructive reset is restricted to this fixture's uniquely named disposable database.
        // Only the migration-owner test connection can disable append-only guards.
        if (!ledger.Database.GetDbConnection().Database.StartsWith("identity_tests_", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to reset a non-test ledger database.");
        await scope.ServiceProvider.GetRequiredService<TransactionsDbContext>().Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE transactions.transactions DISABLE TRIGGER USER; ALTER TABLE transactions.reversal_requests DISABLE TRIGGER USER; TRUNCATE transactions.reversal_requests, transactions.transactions; ALTER TABLE transactions.transactions ENABLE TRIGGER USER; ALTER TABLE transactions.reversal_requests ENABLE TRIGGER USER; COMMIT;");
        await ledger.Database.ExecuteSqlRawAsync("""
            BEGIN;
            ALTER TABLE ledger.entries DISABLE TRIGGER USER;
            ALTER TABLE ledger.journals DISABLE TRIGGER USER;
            ALTER TABLE ledger.account_balances DISABLE TRIGGER USER;
            ALTER TABLE ledger.accounts DISABLE TRIGGER USER;
            TRUNCATE ledger.entries, ledger.journals, ledger.account_balances, ledger.accounts;
            ALTER TABLE ledger.entries ENABLE TRIGGER USER;
            ALTER TABLE ledger.journals ENABLE TRIGGER USER;
            ALTER TABLE ledger.account_balances ENABLE TRIGGER USER;
            ALTER TABLE ledger.accounts ENABLE TRIGGER USER;
            COMMIT;
            """);
        await scope.ServiceProvider.GetRequiredService<RiskLimitsDbContext>().Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE risk_limits.rules DISABLE TRIGGER USER; ALTER TABLE risk_limits.usage_operations DISABLE TRIGGER USER; TRUNCATE risk_limits.period_usage, risk_limits.usage_operations, risk_limits.rules; ALTER TABLE risk_limits.rules ENABLE TRIGGER USER; ALTER TABLE risk_limits.usage_operations ENABLE TRIGGER USER; COMMIT;");
        await scope.ServiceProvider.GetRequiredService<TransfersDbContext>().Database.ExecuteSqlRawAsync("BEGIN; ALTER TABLE transfers.transfers DISABLE TRIGGER USER; ALTER TABLE transfers.quotes DISABLE TRIGGER USER; ALTER TABLE transfers.idempotency DISABLE TRIGGER USER; TRUNCATE transfers.idempotency, transfers.transfers, transfers.quotes; ALTER TABLE transfers.transfers ENABLE TRIGGER USER; ALTER TABLE transfers.quotes ENABLE TRIGGER USER; ALTER TABLE transfers.idempotency ENABLE TRIGGER USER; COMMIT;");
        await wallets.Database.ExecuteSqlRawAsync("TRUNCATE TABLE wallets.wallet_status_history, wallets.wallets CASCADE;");
        await beneficiaries.Database.ExecuteSqlRawAsync("TRUNCATE TABLE beneficiaries.beneficiaries CASCADE;");
        await customers.Database.ExecuteSqlRawAsync("TRUNCATE TABLE customers.customer_status_history, customers.kyc_status_history, customers.customers CASCADE;");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE identity.refresh_tokens, identity.devices, identity.otp_challenges, identity.user_roles, identity.users, identity.role_permissions, identity.permissions, identity.roles CASCADE;");
        await Services.InitializeIdentityModuleAsync();
        await Services.InitializeCustomersModuleAsync();
        await Services.InitializeLedgerModuleAsync();
        await Services.InitializeWalletsModuleAsync();
        await Services.InitializeBeneficiariesModuleAsync();
        Otp.Clear();
    }

    public async Task InDatabaseAsync(Func<IdentityDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
    }
    public Task ExpireAsync(Guid id) => InDatabaseAsync(async db =>
    {
        (await db.OtpChallenges.SingleAsync(x => x.Id == id)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    });
    public async Task<Guid> LatestChallengeAsync(string email, OtpPurpose purpose)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.OtpChallenges.Where(x => x.Purpose == purpose && x.Target == email.ToUpperInvariant()).OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstAsync();
    }
}

public sealed class FaultInjectingOtpChallengeService(IdentityService inner, IdentityPostgresFactory factory) : IOtpChallengeService
{
    public async Task<OtpChallengeIssued> CreateAsync(CreateOtpChallenge request, CancellationToken cancellationToken)
    {
        if (factory.TakeOtpCreateFailure())
            throw new InvalidOperationException("Injected Identity OTP failure.");
        var issued = await inner.CreateAsync(request, cancellationToken);
        if (factory.TakeAfterOtpCreateFailure())
            throw new InvalidOperationException("Injected response loss after Identity OTP creation.");
        return issued;
    }
    public Task<OtpVerificationStatus> VerifyAsync(VerifyOtpChallenge request, CancellationToken cancellationToken) => inner.VerifyAsync(request, cancellationToken);
    public Task<OtpVerificationReceiptStatus> GetVerificationReceiptAsync(VerifyOtpChallenge request, CancellationToken cancellationToken) => inner.GetVerificationReceiptAsync(request, cancellationToken);
}

public sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _utcNow = initial;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
}

public sealed class CapturingOtpSender : IOtpSender
{
    private readonly ConcurrentDictionary<Guid, string> _codes = new();
    public int SentCount => _codes.Count;
    public Task SendAsync(Guid challengeId, string target, string code, OtpPurpose purpose, CancellationToken cancellationToken)
    {
        _codes[challengeId] = code;
        return Task.CompletedTask;
    }
    public string Get(Guid id) => _codes[id];
    public void Clear() => _codes.Clear();
}

public sealed class TransferFaults : ITransferCheckpoint
{
    private int _remaining;
    public string? FailurePoint { get; private set; }
    public bool SerializationFailure { get; private set; }
    public Func<string, Guid, Task>? Observe { get; set; }
    public void Fail(string point, int count = 1, bool serialization = false)
    { FailurePoint = point; _remaining = count; SerializationFailure = serialization; }
    public void Reset() { FailurePoint = null; _remaining = 0; Observe = null; SerializationFailure = false; }
    public async Task ReachedAsync(string point, Guid transferId, CancellationToken ct)
    {
        if (Observe is { } observe) await observe(point, transferId);
        if (point == FailurePoint && Interlocked.Decrement(ref _remaining) >= 0)
        {
            if (SerializationFailure) throw new PostgresException("Injected serialization failure", "ERROR", "ERROR", "40001");
            throw new InvalidOperationException("Injected transfer failure at " + point);
        }
    }
}

public sealed class ReversalFaults : IReversalCheckpoint
{
    private int _remaining;
    public string? FailurePoint { get; private set; }
    public bool SerializationFailure { get; private set; }
    public void Fail(string point, int count = 1, bool serialization = false)
    { FailurePoint = point; _remaining = count; SerializationFailure = serialization; }
    public void Reset() { FailurePoint = null; _remaining = 0; SerializationFailure = false; }
    public Task ReachedAsync(string point, Guid id, CancellationToken ct)
    {
        if (point == FailurePoint && Interlocked.Decrement(ref _remaining) >= 0)
        {
            if (SerializationFailure) throw new PostgresException("Injected serialization failure", "ERROR", "ERROR", "40001");
            throw new InvalidOperationException("Injected reversal failure at " + point);
        }
        return Task.CompletedTask;
    }
}

public sealed class MessagingFaults : IMessagingCheckpoint, INotificationCheckpoint
{
    private int _remaining;
    private string? _point;
    private bool _crash;
    public void Fail(string point, int count = 1, bool crash = false) { _point = point; _remaining = count; _crash = crash; }
    public void Reset() { _point = null; _remaining = 0; }
    public Task ReachedAsync(string point, Guid id, CancellationToken ct)
    {
        if (point == _point && Interlocked.Decrement(ref _remaining) >= 0)
        {
            if (_crash) throw new MessagingCrashException();
            throw new InvalidOperationException("Injected consumer failure.");
        }
        return Task.CompletedTask;
    }
}

public sealed class DelegatingReportCheckpoint(IdentityPostgresFactory factory):DigitalWallet.Reporting.Contracts.IReportCheckpoint
{
    public Task ReachedAsync(string point,Guid id,CancellationToken ct)=>factory.ReportCheckpointOverride?.ReachedAsync(point,id,ct)??Task.CompletedTask;
}
