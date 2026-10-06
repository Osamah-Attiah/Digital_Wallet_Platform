using DigitalWallet.Caching;
using DigitalWallet.Administration;
using DigitalWallet.Audit;
using DigitalWallet.Reporting;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Transfers;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transactions;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Identity;
using DigitalWallet.Customers;
using DigitalWallet.Wallets;
using DigitalWallet.Ledger;
using DigitalWallet.Beneficiaries;
using DigitalWallet.RiskLimits;
using DigitalWallet.Messaging;
using DigitalWallet.Notifications;
using DigitalWallet.Api.Composition;
using DigitalWallet.Api.Foundation.Observability;
using DigitalWallet.SharedKernel.Correlation;
namespace DigitalWallet.Api.Hosting;

// Shared composition; only entrypoint hosts decide which background services exist.
public static class WalletComposition
{
    public static bool RunsWorkers(IConfiguration configuration) => configuration["Hosting:Role"] is null or "Combined" or "Worker";
    public static IServiceCollection AddWalletModules(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration["Hosting:Role"] is { } role && role is not ("Api" or "Worker" or "Migrator" or "Combined"))
            throw new InvalidOperationException("Invalid host role.");
        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(p => p.GetRequiredService<CorrelationContext>());
        services.AddCachingModule(configuration);
        services.AddIdentityModule(configuration);
        services.AddCustomersModule(configuration);
        services.AddWalletsModule(configuration);
        services.AddLedgerModule(configuration);
        services.AddScoped<DigitalWallet.Ledger.Application.LedgerReconciliationService>();
        services.AddScoped<DigitalWallet.Ledger.Contracts.ILedgerReconciliation, MonitoredReconciliation>();
        services.AddBeneficiariesModule(configuration);
        services.AddRiskLimitsModule(configuration);
        services.AddTransfersModule(configuration);
        services.AddTransactionsModule(configuration);
        services.AddMessagingModule(configuration);
        services.AddAuditModule(configuration);
        services.AddAdministrationModule(configuration);
        services.AddReportingModule(configuration);
        services.AddNotificationsModule(configuration);
        services.AddScoped<IReportCoordinator, ReportCoordinator>();
        services.AddScoped<ITransactionCoordinator, TransactionCoordinator>();
        services.AddScoped<ITransferCoordinator, TransferCoordinator>();
        return services;
    }
}
