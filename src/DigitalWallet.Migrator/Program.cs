using DigitalWallet.Api.Hosting;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Api.Foundation.Observability;
using DigitalWallet.Migrator;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Hosting:Role"] = "Migrator";
builder.Configuration["Hosting:ServiceName"] = "digitalwallet.migrator";
ContainerConfiguration.Load(builder.Configuration);
builder.AddWalletObservability();
builder.Services.AddWalletModules(builder.Configuration);
ProductionHardening.Enforce(builder);
await using var app = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; timeout.Cancel(); };
using var shutdown = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
    ? System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; timeout.Cancel(); }) : null;
try
{
    var reconcile = args.Contains("--reconcile", StringComparer.Ordinal);
    await MigrationOrchestrator.RunAsync(app.Services, builder.Configuration, reconcile || args.Contains("--verify", StringComparer.Ordinal), timeout.Token);
    if (args.Contains("--seed-synthetic-demo", StringComparer.Ordinal))
        await SyntheticDemoSeed.RunAsync(app.Services, builder.Configuration, timeout.Token);
    if (reconcile)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<DigitalWallet.Ledger.Contracts.ILedgerReconciliation>().RunAsync(timeout.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { reconciliationFindings = report.Findings.Count }));
        if (report.Findings.Count != 0) Environment.ExitCode = 1;
    }
}
catch { Console.Error.WriteLine("Migration failed. Runtime hosts must remain stopped; inspect restricted database diagnostics."); Environment.ExitCode = 1; }
