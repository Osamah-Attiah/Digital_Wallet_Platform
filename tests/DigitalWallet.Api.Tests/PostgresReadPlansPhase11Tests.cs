using System.Text.Json;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class PostgresReadPlansPhase11Tests(IdentityDatabaseFixture database) : IClassFixture<IdentityDatabaseFixture>
{
    [Fact, Trait("Category", "PerformanceSmoke")]
    public async Task Dense_read_tables_use_keyset_indexes_and_keep_reversal_lookup_in_one_SQL_query()
    {
        await using var factory = new IdentityPostgresFactory(database.ConnectionString); await factory.ResetAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        // Synthetic disposable data only. No production database is ever seeded, truncated or modified.
        Assert.StartsWith("identity_tests_", connection.Database);
        const string subject = "11111111-1111-1111-1111-111111111111";
        await using var seed = new NpgsqlCommand("""
            INSERT INTO customers.customers ("Id","IdentityUserId","FirstName","LastName","PreferredLanguage","Status","KycStatus","CreatedAt","UpdatedAt","ConcurrencyVersion")
            SELECT gen_random_uuid(), gen_random_uuid(), 'Synthetic','Plan','ar-SA','Active','Verified',now()-i*interval '1 second',now(),gen_random_uuid() FROM generate_series(1,10000) i;
            INSERT INTO audit.logs ("Id","ActorId","ActorType","Action","ResourceType","ResourceId","Result","OccurredAt","CorrelationId")
            SELECT gen_random_uuid(), @subject, 'Staff','audit.search','Synthetic','collection','Success',now()-i*interval '1 second','synthetic-plan' FROM generate_series(1,10000) i;
            INSERT INTO transactions.transactions ("Id","TransferId","PublicReference","Type","JournalId","SenderCustomerId","RecipientCustomerId","SenderWalletId","RecipientWalletId","AmountMinor","FeeMinor","SenderDebitMinor","RecipientCreditMinor","Currency","MaskedSenderName","MaskedRecipientName","OccurredAt")
            SELECT id,id,'PLAN-'||i,'InternalTransfer',gen_random_uuid(),@subject,gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),100,0,100,100,'SAR','S***','R***',now()-i*interval '1 second' FROM (SELECT i,gen_random_uuid() id FROM generate_series(1,10000) i) x;
            INSERT INTO notifications.notifications ("Id","CustomerId","EventId","AggregateId","Kind","Direction","AmountMinor","Currency","CreatedAt")
            SELECT gen_random_uuid(),@subject,gen_random_uuid(),gen_random_uuid(),'TransferCompleted','Incoming',100,'SAR',now()-i*interval '1 second' FROM generate_series(1,10000) i;
            INSERT INTO reporting.jobs ("Id","OwnerId","IdempotencyKey","Fingerprint","Type","Status","From","To","IncludesPii","CreatedAt","ExpiresAt","CorrelationId","Attempts")
            SELECT gen_random_uuid(),@subject,'plan-'||i,'synthetic','Activity','Queued',now()-interval '1 day',now(),false,now()-i*interval '1 second',now()+interval '1 day','synthetic-plan',0 FROM generate_series(1,10000) i;
            ANALYZE customers.customers; ANALYZE audit.logs; ANALYZE transactions.transactions; ANALYZE notifications.notifications; ANALYZE reporting.jobs;
            """, connection);
        seed.Parameters.AddWithValue("subject", Guid.Parse(subject)); await seed.ExecuteNonQueryAsync();
        var plans = new Dictionary<string, JsonElement>();
        foreach (var (name, sql, indexExpected) in new[]
        {
            ("permissions", "SELECT DISTINCT rp.\"PermissionCode\" FROM identity.user_roles ur JOIN identity.role_permissions rp ON ur.\"RoleId\"=rp.\"RoleId\" WHERE ur.\"UserId\"=@subject", false),
            ("dashboard-count", "SELECT count(*) FROM customers.customers", false),
            ("customers", "SELECT \"Id\",\"CreatedAt\" FROM customers.customers WHERE \"CreatedAt\" < now() ORDER BY \"CreatedAt\" DESC,\"Id\" DESC LIMIT 21", true),
            ("customers-status", "SELECT \"Id\",\"CreatedAt\" FROM customers.customers WHERE \"Status\"='Active' AND \"CreatedAt\" < now() ORDER BY \"CreatedAt\" DESC,\"Id\" DESC LIMIT 21", true),
            ("customers-kyc", "SELECT \"Id\",\"CreatedAt\" FROM customers.customers WHERE \"KycStatus\"='Verified' AND \"CreatedAt\" < now() ORDER BY \"CreatedAt\" DESC,\"Id\" DESC LIMIT 21", true),
            ("audit", "SELECT * FROM audit.logs WHERE \"ActorId\"=@subject AND \"OccurredAt\"<now() ORDER BY \"OccurredAt\" DESC,\"Id\" DESC LIMIT 21", true),
            ("history", "SELECT t.*, (SELECT r.\"Id\" FROM transactions.transactions r WHERE r.\"ReversalOf\"=t.\"Id\") AS reversal FROM transactions.transactions t WHERE t.\"OccurredAt\"<now() ORDER BY t.\"OccurredAt\" DESC,t.\"Id\" DESC LIMIT 21", true),
            ("notifications", "SELECT * FROM notifications.notifications WHERE \"CustomerId\"=@subject ORDER BY \"CreatedAt\" DESC,\"Id\" DESC LIMIT 21", true),
            ("reports", "SELECT \"Id\",\"Status\",\"CreatedAt\" FROM reporting.jobs WHERE \"OwnerId\"=@subject AND \"CreatedAt\"<now() ORDER BY \"CreatedAt\" DESC,\"Id\" DESC LIMIT 21", true)
        })
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, connection); explain.Parameters.AddWithValue("subject", Guid.Parse(subject));
            var json = (string)(await explain.ExecuteScalarAsync())!; plans[name] = JsonDocument.Parse(json).RootElement.Clone();
            if (indexExpected) Assert.Contains("Index", json); // Structural gate, no timing threshold.
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("WALLET_PLAN_OUTPUT") ?? "/tmp/wallet-phase11-dense-plans.json",
            JsonSerializer.Serialize(new { rowsPerList = 10000, subject = "fixed synthetic UUID", plans }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
