using System.Xml.Linq;
using Xunit;

namespace DigitalWallet.ArchitectureTests;

public sealed class ProjectReferenceRulesTests
{
    private static readonly string[] ModuleProjects =
    [
        "DigitalWallet.Identity", "DigitalWallet.Customers", "DigitalWallet.Wallets", "DigitalWallet.Beneficiaries",
        "DigitalWallet.RiskLimits", "DigitalWallet.Transfers", "DigitalWallet.Ledger", "DigitalWallet.Transactions",
        "DigitalWallet.Notifications", "DigitalWallet.Administration", "DigitalWallet.Reporting", "DigitalWallet.Audit", "DigitalWallet.Messaging", "DigitalWallet.Caching"
    ];

    [Fact]
    public void Modules_follow_the_documented_reference_policy()
    {
        var root = FindRepositoryRoot();

        foreach (var module in ModuleProjects)
        {
            var project = Path.Combine(root, "src", "Modules", module, $"{module}.csproj");
            var expected = module switch
            {
                "DigitalWallet.Customers" => new[] { "DigitalWallet.Customers.Contracts", "DigitalWallet.SharedKernel" },
                "DigitalWallet.Identity" => new[] { "DigitalWallet.Identity.Contracts", "DigitalWallet.SharedKernel", "DigitalWallet.Caching.Contracts" },
                "DigitalWallet.Wallets" => new[] { "DigitalWallet.Customers.Contracts", "DigitalWallet.Ledger.Contracts", "DigitalWallet.SharedKernel", "DigitalWallet.Wallets.Contracts" },
                "DigitalWallet.Beneficiaries" => new[] { "DigitalWallet.Beneficiaries.Contracts", "DigitalWallet.Customers.Contracts", "DigitalWallet.Identity.Contracts", "DigitalWallet.SharedKernel", "DigitalWallet.Wallets.Contracts" },
                "DigitalWallet.RiskLimits" => new[] { "DigitalWallet.RiskLimits.Contracts", "DigitalWallet.SharedKernel" },
                "DigitalWallet.Transfers" => new[] { "DigitalWallet.Transfers.Contracts", "DigitalWallet.Beneficiaries.Contracts", "DigitalWallet.Customers.Contracts", "DigitalWallet.Identity.Contracts", "DigitalWallet.Ledger.Contracts", "DigitalWallet.RiskLimits.Contracts", "DigitalWallet.SharedKernel", "DigitalWallet.Wallets.Contracts" },
                "DigitalWallet.Transactions" => new[] { "DigitalWallet.Customers.Contracts", "DigitalWallet.Identity.Contracts", "DigitalWallet.Ledger.Contracts", "DigitalWallet.SharedKernel", "DigitalWallet.Transfers.Contracts", "DigitalWallet.Transactions.Contracts" },
                "DigitalWallet.Ledger" => new[] { "DigitalWallet.Ledger.Contracts", "DigitalWallet.SharedKernel" },
                "DigitalWallet.Notifications" => new[] { "DigitalWallet.Customers.Contracts", "DigitalWallet.Messaging.Contracts", "DigitalWallet.SharedKernel" },
                "DigitalWallet.Messaging" => new[] { "DigitalWallet.Messaging.Contracts", "DigitalWallet.SharedKernel" },
                "DigitalWallet.Administration" => new[]{"DigitalWallet.Caching.Contracts","DigitalWallet.SharedKernel","DigitalWallet.Customers.Contracts","DigitalWallet.Wallets.Contracts","DigitalWallet.Identity.Contracts","DigitalWallet.Transactions.Contracts","DigitalWallet.Transfers.Contracts","DigitalWallet.Ledger.Contracts","DigitalWallet.Messaging.Contracts"},
                "DigitalWallet.Reporting" => new[]{"DigitalWallet.SharedKernel","DigitalWallet.Reporting.Contracts","DigitalWallet.Customers.Contracts","DigitalWallet.Transactions.Contracts","DigitalWallet.Transfers.Contracts","DigitalWallet.Identity.Contracts","DigitalWallet.Audit.Contracts","DigitalWallet.Messaging.Contracts"},
                "DigitalWallet.Audit" => new[]{"DigitalWallet.SharedKernel","DigitalWallet.Audit.Contracts"},
                "DigitalWallet.Caching" => new[]{"DigitalWallet.Caching.Contracts"},
                _ => new[] { "DigitalWallet.SharedKernel" }
            };
            Assert.Equal(expected.Order(), GetProjectReferences(project).Order());
        }
    }

    [Fact]
    public void Shared_kernel_has_no_platform_project_references()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "src", "DigitalWallet.SharedKernel", "DigitalWallet.SharedKernel.csproj");

        Assert.Empty(GetProjectReferences(project));
    }

    [Fact]
    public void Api_is_the_only_composition_root_for_modules()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "src", "DigitalWallet.Api", "DigitalWallet.Api.csproj");
        var expected = ModuleProjects.Append("DigitalWallet.SharedKernel").Append("DigitalWallet.Customers.Contracts").Append("DigitalWallet.Ledger.Contracts").Append("DigitalWallet.Identity.Contracts").Append("DigitalWallet.Wallets.Contracts").Append("DigitalWallet.Beneficiaries.Contracts").Append("DigitalWallet.Messaging.Contracts").Append("DigitalWallet.Audit.Contracts").Append("DigitalWallet.Reporting.Contracts").Append("DigitalWallet.Transactions.Contracts").Append("DigitalWallet.Caching.Contracts").OrderBy(name => name);

        Assert.Equal(expected, GetProjectReferences(project).OrderBy(name => name));
    }

    [Theory]
    [InlineData("DigitalWallet.Customers.Contracts")]
    [InlineData("DigitalWallet.Ledger.Contracts")]
    [InlineData("DigitalWallet.Identity.Contracts")]
    [InlineData("DigitalWallet.Wallets.Contracts")]
    [InlineData("DigitalWallet.Beneficiaries.Contracts")]
    [InlineData("DigitalWallet.RiskLimits.Contracts")]
    [InlineData("DigitalWallet.Transfers.Contracts")]
    [InlineData("DigitalWallet.Messaging.Contracts")]
    [InlineData("DigitalWallet.Audit.Contracts")]
    [InlineData("DigitalWallet.Reporting.Contracts")]
    [InlineData("DigitalWallet.Transactions.Contracts")]
    [InlineData("DigitalWallet.Caching.Contracts")]
    public void Contracts_have_no_implementation_framework_or_persistence_dependencies(string name)
    {
        var project = Path.Combine(FindRepositoryRoot(), "src", "Modules", name, name + ".csproj");
        Assert.Empty(GetProjectReferences(project));
        Assert.Empty(XDocument.Load(project).Descendants("PackageReference"));
        Assert.Empty(XDocument.Load(project).Descendants("FrameworkReference"));
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Ledger_inner_layers_do_not_import_infrastructure_or_HTTP(string layer)
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Ledger", layer);
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("using DigitalWallet.Ledger.Infrastructure", source);
            Assert.DoesNotContain("using Microsoft.EntityFrameworkCore", source);
            Assert.DoesNotContain("using Microsoft.AspNetCore", source);
            Assert.DoesNotContain("using Npgsql", source);
        }
    }

    [Fact]
    public void Wallet_has_no_financial_balance_and_modules_do_not_access_each_others_schemas()
    {
        var modules = Path.Combine(FindRepositoryRoot(), "src", "Modules");
        Assert.DoesNotContain("Balance", File.ReadAllText(Path.Combine(modules, "DigitalWallet.Wallets", "Domain", "Wallet.cs")));
        foreach (var (module, forbidden) in new[]
        {
            ("DigitalWallet.Wallets", new[] { "ledger.accounts", "ledger.journals", "ledger.entries", "ledger.account_balances" }),
            ("DigitalWallet.Ledger", new[] { "wallets.wallets", "customers.customers", "transfers.transfers", "DigitalWallet.Transfers" })
        })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(modules, module), "*", SearchOption.AllDirectories)
                         .Where(x => (x.EndsWith(".cs", StringComparison.Ordinal) || x.EndsWith(".sql", StringComparison.Ordinal)) &&
                                     !x.Contains("/obj/", StringComparison.Ordinal) && !x.Contains("/bin/", StringComparison.Ordinal)))
            {
                var source = File.ReadAllText(file);
                foreach (var name in forbidden) Assert.DoesNotContain(name, source);
            }
        }
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Risk_limits_inner_layers_are_independent_of_persistence_and_HTTP(string layer)
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.RiskLimits", layer);
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var forbidden in new[] { "DigitalWallet.RiskLimits.Infrastructure", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql" })
                Assert.DoesNotContain(forbidden, source);
        }
    }
    [Fact]
    public void Risk_limits_has_no_cross_schema_SQL_or_other_module_implementation_imports()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.RiskLimits");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("/obj/") && !x.Contains("/bin/")))
        {
            var source = File.ReadAllText(file);
            foreach (var module in ModuleProjects.Where(x => x != "DigitalWallet.RiskLimits"))
            {
                Assert.DoesNotContain("using " + module, source);
                Assert.DoesNotContain(module.Replace("DigitalWallet.", "").ToLowerInvariant() + ".", source);
            }
            Assert.DoesNotContain("double", source); Assert.DoesNotContain("float", source);
        }
    }
    [Fact]
    public void Risk_contracts_do_not_leak_transactions_entities_or_allow_client_supplied_fees()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.RiskLimits.Contracts", "RiskLimitsContracts.cs"));
        foreach (var forbidden in new[] { "DbContext", "DbConnection", "DbTransaction", "Npgsql", "Microsoft.", "DigitalWallet.RiskLimits.Domain" })
            Assert.DoesNotContain(forbidden, source);
        var request = source.Split("record AssessTransfer(")[1].Split(");")[0];
        Assert.DoesNotContain("Fee", request); Assert.DoesNotContain("EvaluatedAt", request);
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Transfers_inner_layers_do_not_import_persistence_or_HTTP(string layer)
    {
        var dir = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Transfers", layer);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "System.Data", "DigitalWallet.Transfers.Infrastructure" })
                Assert.DoesNotContain(name, source);
        }
    }
    [Fact]
    public void Transfers_has_no_cross_schema_access_or_independent_financial_transactions()
    {
        var dir = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Transfers");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("/obj/") && !x.Contains("/bin/")))
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "customers.customers", "wallets.wallets", "beneficiaries.beneficiaries", "ledger.", "risk_limits.", "TransactionScope", "BeginTransactionAsync", "CommitAsync" })
                Assert.DoesNotContain(name, source);
        }
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Transactions_inner_layers_do_not_import_persistence_or_HTTP(string layer)
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Transactions", layer);
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "System.Data", "DigitalWallet.Transactions.Infrastructure" })
                Assert.DoesNotContain(name, source);
        }
    }

    [Fact]
    public void Transactions_never_queries_other_schemas_or_commits_participant_transactions()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Transactions");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("/obj/") && !x.Contains("/bin/")))
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "transfers.transfers", "ledger.journals", "ledger.entries", "ledger.account_balances", "customers.customers", "wallets.wallets", "risk_limits.", "identity.users", "TransactionScope" })
                Assert.DoesNotContain(name, source);
            if (file.EndsWith("TransactionsTransactionParticipant.cs", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("BeginTransactionAsync", source); Assert.DoesNotContain("CommitAsync", source);
            }
        }
    }

    [Fact]
    public void Notifications_consume_contracts_without_financial_implementations_or_cross_schema_SQL()
    {
        var dir = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Notifications");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("/obj/") && !x.Contains("/bin/")))
        {
            var source = File.ReadAllText(file);
            foreach (var name in new[] { "DigitalWallet.Transfers", "DigitalWallet.Transactions", "transfers.transfers", "transactions.transactions", "ledger.", "identity.users", "customers.customers" })
                Assert.DoesNotContain(name, source);
        }
    }
    [Fact]
    public void Financial_composition_only_enlists_the_outbox_writer_and_never_contacts_RabbitMQ()
    {
        foreach (var name in new[] { "TransferCoordinator.cs", "TransactionCoordinator.cs" })
        {
            var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "DigitalWallet.Api", "Composition", name));
            Assert.Contains("OutboxWriter.EnlistAsync(connection, transaction", source);
            Assert.True(source.IndexOf("outbox.AppendAsync", StringComparison.Ordinal) < source.IndexOf("transaction.CommitAsync", StringComparison.Ordinal));
            foreach (var forbidden in new[] { "RabbitMQ", "RabbitTransport", "IMessagePublisher", "HttpClient", "PublishAsync", "Task.Run" }) Assert.DoesNotContain(forbidden, source);
        }
        var writer = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Messaging", "OutboxWriter.cs"));
        foreach (var forbidden in new[] { "RabbitTransport", "IMessagePublisher", "BeginTransactionAsync", "CommitAsync" }) Assert.DoesNotContain(forbidden, writer);
    }
    [Fact]
    public void Messaging_infrastructure_cannot_depend_on_financial_implementations()
    {
        var project = Path.Combine(FindRepositoryRoot(), "src", "Modules", "DigitalWallet.Messaging", "DigitalWallet.Messaging.csproj");
        Assert.Equal(new[] { "DigitalWallet.Messaging.Contracts", "DigitalWallet.SharedKernel" }, GetProjectReferences(project).Order());
    }

    [Theory]
    [InlineData("DigitalWallet.Administration")]
    [InlineData("DigitalWallet.Reporting")]
    [InlineData("DigitalWallet.Audit")]
    public void Phase10_modules_read_only_owned_schemas_and_value_contracts(string module)
    {
        var directory=Path.Combine(FindRepositoryRoot(),"src","Modules",module);
        foreach(var file in Directory.EnumerateFiles(directory,"*.cs",SearchOption.AllDirectories).Where(x=>!x.Contains("/obj/")&&!x.Contains("/bin/")))
        {
            var source=File.ReadAllText(file);
            foreach(var forbidden in new[]{"identity.users","customers.customers","wallets.wallets","ledger.accounts","ledger.journals","ledger.entries","ledger.account_balances","transactions.transactions","transfers.transfers","risk_limits.rules","messaging.outbox"})Assert.DoesNotContain(forbidden,source);
            foreach(var name in ModuleProjects.Where(x=>x!=module))
                foreach(var layer in new[]{".Infrastructure",".Persistence",".Application",".Services",".Domain"})Assert.DoesNotContain("using "+name+layer,source);
        }
    }
    [Fact]
    public void Report_job_outbox_and_audit_share_the_API_owned_transaction()
    {
        var source = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "DigitalWallet.Api", "Composition", "ReportCoordinator.cs")), @"\s+", "");
        Assert.Contains("OutboxWriter.EnlistAsync(connection,tx", source);
        Assert.Contains("AuditWriter.EnlistAsync(connection,tx", source);
        Assert.Contains("outbox.AppendAsync", source);
        const string creationCommit = "awaittx.CommitAsync(ct);returnj.Receipt()";
        Assert.Contains(creationCommit, source);
        Assert.True(source.IndexOf("outbox.AppendAsync", StringComparison.Ordinal) < source.IndexOf(creationCommit, StringComparison.Ordinal));
        foreach(var forbidden in new[]{"RabbitTransport","IMessagePublisher","PublishAsync","HttpClient","Task.Run"})Assert.DoesNotContain(forbidden,source);
    }
    private static IEnumerable<string> GetProjectReferences(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include")))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Digital_Wallet_Platform.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
