using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalWallet.Administration;
using DigitalWallet.Audit;
using DigitalWallet.Audit.Contracts;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Services;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.Reporting;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Wallets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
namespace DigitalWallet.Api.Tests;

public sealed class AdminPhase10PostgresTests : IClassFixture<IdentityDatabaseFixture>, IClassFixture<RabbitDatabaseFixture>, IAsyncLifetime
{
    private readonly IdentityPostgresFactory factory;
    private readonly ReportFaults faults = new();
    private readonly RabbitDatabaseFixture rabbit;
    public AdminPhase10PostgresTests(IdentityDatabaseFixture db, RabbitDatabaseFixture rabbit)
    {
        this.rabbit = rabbit;
        factory = new IdentityPostgresFactory(db.ConnectionString, new Dictionary<string, string?> { ["Messaging:RabbitUri"] = rabbit.ConnectionString, ["Messaging:Prefix"] = "phase10_" + Guid.NewGuid().ToString("N"), ["Messaging:PublishTimeoutSeconds"] = "2", ["Messaging:LeaseSeconds"] = "5", ["Messaging:RetryBaseMilliseconds"] = "100" });
        factory.ReportCheckpointOverride = faults;
    }
    public async Task InitializeAsync() { await factory.ResetAsync(); }
    public async Task DisposeAsync() { await factory.DisposeAsync(); }
    private async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> action) { await using var s = factory.Services.CreateAsyncScope(); return await action(s.ServiceProvider); }
    private async Task<User> UserAsync(params string[] permissions)
    {
        var client = factory.CreateClient(); var email = Guid.NewGuid().ToString("N") + "@test.local"; var device = new DeviceRequest(Guid.NewGuid().ToString("N"), "Test");
        var r = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", device));
        var challenge = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var auth = await Read<AuthResult>(await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, factory.Otp.Get(challenge), device)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        var id = await Scope(sp => sp.GetRequiredService<IdentityDbContext>().RefreshTokens.Where(x => x.FamilyId == auth.SessionId).Select(x => x.UserId).SingleAsync());
        var u = new User(id, auth, client); await Grant(u, permissions); return u;
    }
    private Task<Guid> Grant(User u, params string[] permissions) => Scope(async sp =>
    {
        var db = sp.GetRequiredService<IdentityDbContext>(); var role = new ApplicationRole { Id = Guid.NewGuid(), Name = "Admin" + Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), DisplayName = "Operations", ConcurrencyStamp = Guid.NewGuid().ToString("D") }; db.Roles.Add(role); db.UserRoles.Add(new() { UserId = u.Id, RoleId = role.Id });
        foreach (var p in permissions.Distinct()) db.RolePermissions.Add(new() { RoleId = role.Id, PermissionCode = p }); await db.SaveChangesAsync(); return role.Id;
    });
    private async Task Mfa(User u) { var otp = await Read<OtpChallengeIssued>(await u.Client.PostAsJsonAsync("/api/v1/admin/session/mfa", new { })); await Read<JsonElement>(await u.Client.PostAsJsonAsync("/api/v1/admin/session/mfa/verify", new { challengeId = otp.ChallengeId, code = factory.Otp.Get(otp.ChallengeId) })); }
    private async Task<CustomerResponse> Customer(string first = "Amina")
    {
        var u = await UserAsync(); return await Read<CustomerResponse>(await u.Client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest(first, "Private", "+966500000001", "ar-SA")), HttpStatusCode.Created);
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK) { Assert.Equal(status, response.StatusCode); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private ReportRequest Request(bool pii = false) => new("CustomerSummary", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1), pii);
    private async Task<ReportReceipt> Create(User user, string key, ReportRequest? request = null)
    { var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/reports") { Content = JsonContent.Create(request ?? Request()) }; r.Headers.Add("Idempotency-Key", key); return await Read<ReportReceipt>(await user.Client.SendAsync(r), HttpStatusCode.Accepted); }
    private async Task Consume(Guid id, IReportCheckpoint? checkpoint = null)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<ReportingDbContext>(); var job = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == id);
        var consumer = checkpoint is null ? sp.GetRequiredService<ReportConsumer>() : ActivatorUtilities.CreateInstance<ReportConsumer>(sp, checkpoint);
        await consumer.ConsumeAsync(WalletEvents.Report(job.Id, job.CreatedAt, job.CorrelationId), default);
    }
    private sealed record User(Guid Id, AuthResult Auth, HttpClient Client);
    private sealed class ReportFaults : IReportCheckpoint
    { public string? Point; public bool Crash = true; public Task ReachedAsync(string point, Guid id, CancellationToken ct) { if (Point == point) { Point = null; if (Crash) throw new ReportCrashException(); throw new InvalidOperationException("Injected checkpoint failure."); } return Task.CompletedTask; } }

    [Fact]
    public async Task Masking_is_default_even_when_PII_permission_is_present_and_reveal_is_explicit_audited()
    {
        var c = await Customer(); var u = await UserAsync("customer.view");
        var masked = await Read<CustomerResponse>(await u.Client.GetAsync($"/api/v1/admin/customers/{c.Id}")); Assert.Contains('*', masked.FirstName); Assert.NotEqual(c.PhoneNumber, masked.PhoneNumber);
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.PostAsJsonAsync($"/api/v1/admin/customers/{c.Id}/pii", new { reason = "Review" })).StatusCode);
        await Grant(u, "customer.pii.view");
        var list = await Read<CustomerSearchPage>(await u.Client.GetAsync("/api/v1/admin/customers/search")); Assert.All(list.Items, x => Assert.Contains('*', x.FirstName));
        Assert.Contains('*', (await Read<CustomerResponse>(await u.Client.GetAsync($"/api/v1/admin/customers/{c.Id}"))).FirstName);
        var pii = await Read<AdminCustomer>(await u.Client.PostAsJsonAsync($"/api/v1/admin/customers/{c.Id}/pii", new { reason = "Review", includePii = true })); Assert.Equal("Amina", pii.FirstName);
        var logs = await Scope(sp => sp.GetRequiredService<AuditDbContext>().Logs.Where(x => x.Action == "customer.pii.view").ToListAsync()); Assert.Contains(logs, x => x.Result == "Denied"); Assert.Contains(logs, x => x.Result == "Success"); Assert.DoesNotContain("Amina", JsonSerializer.Serialize(logs));
    }
    [Theory]
    [InlineData("GET", "customers")]
    [InlineData("GET", "customers/search")]
    [InlineData("GET", "customers/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/pii")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/suspend")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/block")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/unblock")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/kyc/approve")]
    [InlineData("POST", "customers/00000000-0000-0000-0000-000000000001/kyc/reject")]
    [InlineData("GET", "customers/00000000-0000-0000-0000-000000000001/wallets")]
    [InlineData("GET", "wallets/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "wallets/00000000-0000-0000-0000-000000000001/freeze")]
    [InlineData("POST", "wallets/00000000-0000-0000-0000-000000000001/unfreeze")]
    [InlineData("GET", "transactions")]
    [InlineData("GET", "transfers")]
    [InlineData("GET", "transfers/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "transfers/00000000-0000-0000-0000-000000000001/reversal-requests")]
    [InlineData("GET", "reversal-requests")]
    [InlineData("GET", "reversal-requests/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "reversal-requests/00000000-0000-0000-0000-000000000001/otp")]
    [InlineData("POST", "reversal-requests/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("POST", "reversal-requests/00000000-0000-0000-0000-000000000001/reject")]
    [InlineData("GET", "dashboard")]
    [InlineData("GET", "operations/messaging")]
    [InlineData("GET", "audit-logs")]
    [InlineData("POST", "reports")]
    [InlineData("GET", "reports")]
    [InlineData("GET", "reports/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "reports/00000000-0000-0000-0000-000000000001/download")]
    [InlineData("POST", "roles")]
    [InlineData("GET", "roles")]
    [InlineData("PUT", "roles/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "permissions")]
    [InlineData("PUT", "staff/00000000-0000-0000-0000-000000000001/roles")]
    [InlineData("GET", "settings")]
    [InlineData("PUT", "settings/support.locale")]
    [InlineData("GET", "session")]
    [InlineData("POST", "session/mfa")]
    [InlineData("POST", "session/mfa/verify")]
    public async Task All_admin_functions_reject_customer_and_body_permissions(string method, string path)
    {
        var u = await UserAsync(); var r = new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/" + path); if (method != "GET") r.Content = JsonContent.Create(new { permissions = new[] { "customer.pii.view", "report.view", "wallet.freeze" }, canViewAny = true, ownerId = u.Id });
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.SendAsync(r)).StatusCode);
    }
    [Fact]
    public async Task Unknown_resources_return_safe_404_after_permission_check()
    {
        var u = await UserAsync("customer.view", "wallet.view.any", "report.view", "transaction.view.any");
        Assert.Equal(HttpStatusCode.NotFound, (await u.Client.GetAsync($"/api/v1/admin/customers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.Client.GetAsync($"/api/v1/admin/wallets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.Client.GetAsync($"/api/v1/admin/reports/{Guid.NewGuid()}")).StatusCode);
    }
    [Fact]
    public async Task Customer_keysets_handle_equal_timestamps_and_bind_filters_and_actor()
    {
        await Customer("Amina"); await Customer("Badr"); var a = await UserAsync("customer.view"); var b = await UserAsync("customer.view");
        await Scope(async sp => { var db = sp.GetRequiredService<CustomersDbContext>(); await db.Customers.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-1))); return true; });
        var page = await Read<CustomerSearchPage>(await a.Client.GetAsync("/api/v1/admin/customers/search?pageSize=1")); Assert.NotNull(page.NextCursor);
        var next = await Read<CustomerSearchPage>(await a.Client.GetAsync("/api/v1/admin/customers/search?pageSize=1&cursor=" + Uri.EscapeDataString(page.NextCursor!))); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id); Assert.Null(next.NextCursor);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.GetAsync("/api/v1/admin/customers/search?cursor=" + Uri.EscapeDataString(page.NextCursor!))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.GetAsync("/api/v1/admin/customers/search?status=Active&cursor=" + Uri.EscapeDataString(page.NextCursor!))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.GetAsync("/api/v1/admin/customers/search?pageSize=101")).StatusCode);
    }
    [Fact]
    public async Task Audit_has_bounded_dates_filters_and_keysets()
    {
        var u = await UserAsync("audit.view"); await Scope(async sp => { var audit = sp.GetRequiredService<IAuditWriter>(); for (var i = 0; i < 3; i++) await audit.AppendAsync(new(u.Id, "Staff", "test.action", "Customer", i.ToString(), "Success", "phase10", "AdministrativeReview"), default); return true; });
        var page = await Read<AuditPage>(await u.Client.GetAsync("/api/v1/admin/audit-logs?action=test.action&result=Success&pageSize=1")); Assert.Single(page.Items); Assert.NotNull(page.NextCursor);
        var next = await Read<AuditPage>(await u.Client.GetAsync("/api/v1/admin/audit-logs?action=test.action&result=Success&pageSize=1&cursor=" + Uri.EscapeDataString(page.NextCursor!))); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        var from = DateTimeOffset.UtcNow.AddDays(-32).ToString("O"); var to = DateTimeOffset.UtcNow.ToString("O"); Assert.Equal(HttpStatusCode.BadRequest, (await u.Client.GetAsync($"/api/v1/admin/audit-logs?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await u.Client.GetAsync("/api/v1/admin/audit-logs?result=other")).StatusCode);
    }
    [Theory]
    [InlineData("UPDATE audit.logs SET \"Result\"='Failed'")]
    [InlineData("DELETE FROM audit.logs")]
    [InlineData("TRUNCATE audit.logs")]
    public async Task Audit_SQL_is_append_only(string sql)
    {
        await Scope(async sp => { await sp.GetRequiredService<IAuditWriter>().AppendAsync(new(Guid.NewGuid(), "Staff", "test.action", "Customer", "1", "Success", "phase10"), default); var db = sp.GetRequiredService<AuditDbContext>(); var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal("23514", ex.SqlState); Assert.Equal(1, await db.Logs.CountAsync()); return true; });
    }
    // This role identifier is generated solely from a GUID, never from request input.
#pragma warning disable EF1002
    [Fact]
    public async Task Runtime_account_can_insert_audit_but_cannot_update_delete_or_truncate()
    {
        await Scope(async sp =>
        {
            var db = sp.GetRequiredService<AuditDbContext>(); var role = "audit_runtime_" + Guid.NewGuid().ToString("N"); await db.Database.ExecuteSqlRawAsync($"CREATE ROLE {role} NOLOGIN; GRANT USAGE ON SCHEMA audit TO {role}; GRANT SELECT, INSERT ON audit.logs TO {role};");
            try { foreach (var sql in new[] { "UPDATE audit.logs SET \"Result\"='Failed'", "DELETE FROM audit.logs", "TRUNCATE audit.logs" }) { await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE {role}"); var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal("42501", ex.SqlState); await tx.RollbackAsync(); } }
            finally { await db.Database.ExecuteSqlRawAsync($"DROP OWNED BY {role}; DROP ROLE {role};"); }
            return true;
        });
    }

#pragma warning restore EF1002
    [Fact]
    public async Task Audit_rejects_arbitrary_sensitive_payloads()
    {
        await Scope(async sp => { var w = sp.GetRequiredService<IAuditWriter>(); await Assert.ThrowsAsync<DigitalWallet.SharedKernel.Administration.AdminException>(() => w.AppendAsync(new(Guid.NewGuid(), "Staff", "test.action", "Customer", "1", "Success", "phase10", Changes: "{\"password\":\"secret\"}"), default)); Assert.Empty(await sp.GetRequiredService<AuditDbContext>().Logs.ToListAsync()); return true; });
    }
    [Fact]
    public async Task Freeze_requires_session_bound_recent_MFA_and_records_success()
    {
        var owner = await UserAsync(); var c = await Read<CustomerResponse>(await owner.Client.PostAsJsonAsync("/api/v1/me/profile", new CreateProfileRequest("Owner", "Private", null, "ar-SA")), HttpStatusCode.Created);
        await Scope(async sp => { var db = sp.GetRequiredService<CustomersDbContext>(); var row = await db.Customers.SingleAsync(x => x.Id == c.Id); row.Status = CustomerStatus.Active; row.KycStatus = KycStatus.Verified; await db.SaveChangesAsync(); return true; });
        var wallet = await Read<WalletResponse>(await owner.Client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest()), HttpStatusCode.Created);
        var staff = await UserAsync("wallet.freeze", "wallet.unfreeze");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.Client.PostAsJsonAsync($"/api/v1/admin/wallets/{wallet.Id}/freeze", new { reason = "Review", concurrencyVersion = wallet.ConcurrencyVersion, recentMfa = true })).StatusCode);
        await Mfa(staff); var frozen = await Read<WalletResponse>(await staff.Client.PostAsJsonAsync($"/api/v1/admin/wallets/{wallet.Id}/freeze", new { reason = "Review", concurrencyVersion = wallet.ConcurrencyVersion }));
        factory.Time.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(HttpStatusCode.Forbidden, (await staff.Client.PostAsJsonAsync($"/api/v1/admin/wallets/{wallet.Id}/unfreeze", new { reason = "Resolved", concurrencyVersion = frozen.ConcurrencyVersion })).StatusCode);
        Assert.True(await Scope(sp => sp.GetRequiredService<AuditDbContext>().Logs.AnyAsync(x => x.Action == "wallet.freeze" && x.Result == "Success")));
    }
    [Fact]
    public async Task Settings_are_allowlisted_versioned_and_audited()
    {
        var u = await UserAsync("configuration.view", "configuration.manage"); await Mfa(u); var list = await Read<AdminSetting[]>(await u.Client.GetAsync("/api/v1/admin/settings")); var value = list.Single(x => x.Key == "support.locale");
        var change = new { value = "en-US", concurrencyVersion = value.ConcurrencyVersion, reason = "Review" }; Assert.Equal(HttpStatusCode.OK, (await u.Client.PutAsJsonAsync("/api/v1/admin/settings/support.locale", change)).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await u.Client.PutAsJsonAsync("/api/v1/admin/settings/support.locale", change)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.Client.PutAsJsonAsync("/api/v1/admin/settings/connectionString", change)).StatusCode);
        Assert.True(await Scope(sp => sp.GetRequiredService<AuditDbContext>().Logs.AnyAsync(x => x.Action == "configuration.change" && x.Result == "Success")));
    }
    [Fact]
    public async Task Role_changes_cannot_grant_self_or_exceed_actor_permissions()
    {
        var u = await UserAsync("role.manage", "permission.assign"); await Mfa(u); var roles = await Read<AdminRole[]>(await u.Client.GetAsync("/api/v1/admin/roles")); var own = roles.Single(x => x.Permissions.Contains("role.manage"));
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.PutAsJsonAsync($"/api/v1/admin/roles/{own.Id}", new RoleChange("Changed", ["customer.pii.view"], own.ConcurrencyVersion, "Review"))).StatusCode);
        var target = await UserAsync(); var role = await Grant(target, "customer.view"); var r = await Scope(sp => sp.GetRequiredService<IdentityDbContext>().Roles.SingleAsync(x => x.Id == role));
        Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.PutAsJsonAsync($"/api/v1/admin/roles/{role}", new RoleChange("Changed", ["customer.pii.view"], r.ConcurrencyStamp!, "Review"))).StatusCode);
    }
    [Fact]
    public async Task Report_job_outbox_and_audit_commit_together_and_key_replay_is_stable()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); var input = Request(); var first = await Create(u, "same", input); var second = await Create(u, "same", input); Assert.Equal(first.Id, second.Id);
        await Scope(async sp => { Assert.Equal(1, await sp.GetRequiredService<ReportingDbContext>().Jobs.CountAsync()); var e = await sp.GetRequiredService<OutboxDbContext>().Messages.SingleAsync(); Assert.Equal(first.Id, e.AggregateId); Assert.Equal(WalletEvents.ReportRequested, e.EventType); Assert.True(await sp.GetRequiredService<AuditDbContext>().Logs.AnyAsync(x => x.Action == "report.create")); return true; });
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/reports") { Content = JsonContent.Create(input with { IncludePii = true }) }; r.Headers.Add("Idempotency-Key", "same"); await Grant(u, "customer.pii.view"); Assert.Equal(HttpStatusCode.Conflict, (await u.Client.SendAsync(r)).StatusCode);
    }
    [Theory]
    [InlineData("AfterReportJob")]
    [InlineData("AfterReportOutbox")]
    public async Task Report_creation_rollback_leaves_no_job_or_event(string point)
    {
        var u = await UserAsync("report.generate", "customer.view"); faults.Point = point; faults.Crash = false;
        await Scope(async sp => { var coordinator = ActivatorUtilities.CreateInstance<DigitalWallet.Api.Composition.ReportCoordinator>(sp, faults); await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateAsync(new(u.Id, "rollback", "fingerprint", Request(), "phase10"), default)); Assert.Empty(await sp.GetRequiredService<ReportingDbContext>().Jobs.ToListAsync()); Assert.Empty(await sp.GetRequiredService<OutboxDbContext>().Messages.ToListAsync()); return true; });
    }
    [Theory]
    [InlineData("AfterReportClaim")]
    [InlineData("AfterReportFile")]
    [InlineData("AfterReportCommit")]
    [InlineData("BeforeReportCommit")]
    public async Task Report_consumer_crash_and_replay_create_one_file_and_one_inbox(string point)
    {
        await Customer(); var u = await UserAsync("report.generate", "report.view", "customer.view"); var j = await Create(u, "crash"); faults.Point = point; await Assert.ThrowsAsync<ReportCrashException>(() => Consume(j.Id, faults)); factory.Time.Advance(TimeSpan.FromSeconds(121)); await Consume(j.Id); await Consume(j.Id);
        await Scope(async sp => { var db = sp.GetRequiredService<ReportingDbContext>(); Assert.Equal("Completed", (await db.Jobs.SingleAsync()).Status); Assert.Single(await db.Inbox.ToListAsync()); var storage = sp.GetRequiredService<ReportOptions>().StoragePath; Assert.Single(Directory.EnumerateFiles(storage, j.Id.ToString("N") + ".csv")); Assert.DoesNotContain(Directory.EnumerateFiles(storage), x => x.EndsWith(".tmp")); return true; });
    }
    [Fact]
    public async Task Report_file_contains_only_masked_customer_fields_and_UTF8_versioned_columns()
    {
        await Customer(); var u = await UserAsync("report.generate", "report.view", "customer.view"); var j = await Create(u, "masked"); await Consume(j.Id); var download = await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download"); Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType); var bytes = await download.Content.ReadAsByteArrayAsync(); Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3)); var csv = Encoding.UTF8.GetString(bytes); Assert.Contains("schema_version", csv); Assert.Contains("A****", csv); Assert.DoesNotContain("Amina", csv); Assert.DoesNotContain("+966500000001", csv);
    }
    [Fact]
    public async Task Report_execution_and_download_recheck_permissions_and_creator_scope()
    {
        var a = await UserAsync("report.generate", "report.view", "customer.view"); var b = await UserAsync("report.view", "customer.view"); var j = await Create(a, "scope"); await Consume(j.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/admin/reports/{j.Id}")).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).StatusCode);
        await Scope(async sp => { await sp.GetRequiredService<IdentityDbContext>().RolePermissions.Where(x => x.PermissionCode == "customer.view").ExecuteDeleteAsync(); return true; }); Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).StatusCode);
    }
    [Fact]
    public async Task Report_rejects_execution_after_permission_revocation()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); var j = await Create(u, "revoke"); await Scope(async sp => { await sp.GetRequiredService<IdentityDbContext>().RolePermissions.Where(x => x.PermissionCode == "report.generate").ExecuteDeleteAsync(); return true; }); await Consume(j.Id);
        var r = await Read<ReportReceipt>(await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}")); Assert.Equal("Failed", r.Status); Assert.Equal("PERMISSION_REVOKED", r.FailureCode);
    }
    [Fact]
    public async Task Explicit_PII_export_requires_permission_at_request_execution_and_download()
    {
        await Customer(); var u = await UserAsync("report.generate", "report.view", "customer.view"); var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/reports") { Content = JsonContent.Create(Request(true)) }; r.Headers.Add("Idempotency-Key", "pii"); Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.SendAsync(r)).StatusCode);
        await Grant(u, "customer.pii.view"); var j = await Create(u, "pii2", Request(true)); await Consume(j.Id); Assert.Contains("Amina", await (await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).Content.ReadAsStringAsync());
        await Scope(async sp => { await sp.GetRequiredService<IdentityDbContext>().RolePermissions.Where(x => x.PermissionCode == "customer.pii.view").ExecuteDeleteAsync(); return true; }); Assert.Equal(HttpStatusCode.Forbidden, (await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).StatusCode);
    }
    [Fact]
    public async Task Messaging_health_includes_reports_and_notifications_dead_letters()
    {
        var u = await UserAsync("transaction.view.any");
        var transport = factory.Services.GetRequiredService<RabbitTransport>();
        await using var connection = await transport.ConnectAsync();
        await using var channel = await transport.OpenAsync(connection);
        var properties = new RabbitMQ.Client.BasicProperties { Persistent = true };
        await channel.BasicPublishAsync("", transport.ReportDeadQueue, true, properties, new byte[] { 1 });
        await channel.BasicPublishAsync("", transport.DeadQueue, true, properties, new byte[] { 1 });
        var status = await Read<MessagingStatus>(await u.Client.GetAsync("/api/v1/admin/operations/messaging"));
        Assert.True(status.BrokerAvailable);
        Assert.Equal(2u, status.DlqCount);
    }
    [Fact]
    public async Task Report_outage_preserves_queued_job_and_recovers_through_real_RabbitMQ()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); await rabbit.StopAsync(); ReportReceipt j;
        try { j = await Create(u, "outage"); Assert.True(await Scope(async sp => await sp.GetRequiredService<OutboxPublisher>().PublishOneAsync())); Assert.Equal("Queued", (await Read<ReportReceipt>(await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}"))).Status); Assert.True(await Scope(sp => sp.GetRequiredService<OutboxDbContext>().Messages.AnyAsync(x => x.PublishedAt == null))); }
        finally { await rabbit.StartAsync(); }
        factory.Services.GetRequiredService<MessagingOptions>().RabbitUri = rabbit.ConnectionString; factory.Time.Advance(TimeSpan.FromSeconds(10)); await Scope(async sp => await sp.GetRequiredService<OutboxPublisher>().PublishOneAsync()); var transport = factory.Services.GetRequiredService<RabbitTransport>(); await using var connection = await transport.ConnectAsync(); await using var channel = await transport.OpenAsync(connection); Assert.True(await factory.Services.GetRequiredService<RabbitConsumerPump>().ProcessOneAsync(channel, reports: true)); Assert.Equal("Completed", (await Read<ReportReceipt>(await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}"))).Status);
    }
    [Fact]
    public async Task Report_date_bounds_idempotency_download_state_and_expiry_are_enforced()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); Assert.Equal(HttpStatusCode.BadRequest, (await u.Client.PostAsJsonAsync("/api/v1/admin/reports", Request())).StatusCode);
        var j = await Create(u, "expire"); Assert.Equal(HttpStatusCode.Conflict, (await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).StatusCode); await Consume(j.Id); factory.Time.Advance(TimeSpan.FromHours(25)); Assert.Equal(HttpStatusCode.Gone, (await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}/download")).StatusCode);
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/reports") { Content = JsonContent.Create(Request() with { From = DateTimeOffset.UtcNow.AddDays(-40) }) }; r.Headers.Add("Idempotency-Key", "large"); Assert.Equal(HttpStatusCode.BadRequest, (await u.Client.SendAsync(r)).StatusCode);
    }
    [Theory]
    [InlineData("=1+1")]
    [InlineData(" +SUM(A1:A2)")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=cmd")]
    [InlineData("\r\n=cmd")]
    public void CSV_formula_injection_is_neutralized(string input) { Assert.StartsWith("\"'", SafeCsv.Cell(input)); Assert.DoesNotContain('\n', SafeCsv.Cell(input)); }
    [Fact]
    public async Task Concurrent_idempotent_report_requests_have_one_job_event_and_creation_audit()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); var input = Request();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Create(u, "parallel", input))); Assert.Single(receipts.Select(x => x.Id).Distinct());
        await Scope(async sp => { Assert.Equal(1, await sp.GetRequiredService<ReportingDbContext>().Jobs.CountAsync()); Assert.Equal(1, await sp.GetRequiredService<OutboxDbContext>().Messages.CountAsync()); Assert.Equal(1, await sp.GetRequiredService<AuditDbContext>().Logs.CountAsync(x => x.Action == "report.create")); return true; });
    }
    [Fact]
    public async Task Failed_Outbox_insert_rolls_back_report_job_and_creation_audit()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view");
        await Scope(async sp => { await sp.GetRequiredService<OutboxDbContext>().Database.ExecuteSqlRawAsync("CREATE FUNCTION messaging.fail_report() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"EventType\"='ReportRequested' THEN RAISE EXCEPTION 'Injected outbox failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER report_insert_failure BEFORE INSERT ON messaging.outbox FOR EACH ROW EXECUTE FUNCTION messaging.fail_report();"); return true; });
        try
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/reports") { Content = JsonContent.Create(Request()) }; r.Headers.Add("Idempotency-Key", "insert-failure"); Assert.Equal(HttpStatusCode.InternalServerError, (await u.Client.SendAsync(r)).StatusCode);
            await Scope(async sp => { Assert.Empty(await sp.GetRequiredService<ReportingDbContext>().Jobs.ToListAsync()); Assert.Empty(await sp.GetRequiredService<OutboxDbContext>().Messages.ToListAsync()); Assert.False(await sp.GetRequiredService<AuditDbContext>().Logs.AnyAsync(x => x.Action == "report.create")); return true; });
        }
        finally { await Scope(async sp => { await sp.GetRequiredService<OutboxDbContext>().Database.ExecuteSqlRawAsync("DROP FUNCTION messaging.fail_report() CASCADE;"); return true; }); }
    }
    [Fact]
    public async Task Audit_insert_failure_rolls_back_customer_state_and_history()
    {
        var c = await Customer(); var u = await UserAsync("customer.block");
        await Scope(async sp => { await sp.GetRequiredService<CustomersDbContext>().Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CustomerStatus.Active)); await sp.GetRequiredService<AuditDbContext>().Database.ExecuteSqlRawAsync("CREATE FUNCTION audit.fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Injected audit failure'; END $$; CREATE TRIGGER audit_insert_failure BEFORE INSERT ON audit.logs FOR EACH ROW EXECUTE FUNCTION audit.fail_insert();"); return true; });
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await u.Client.PostAsJsonAsync($"/api/v1/admin/customers/{c.Id}/block", new { reason = "Review", concurrencyVersion = c.ConcurrencyVersion })).StatusCode);
            await Scope(async sp => { var db = sp.GetRequiredService<CustomersDbContext>(); Assert.Equal(CustomerStatus.Active, (await db.Customers.SingleAsync(x => x.Id == c.Id)).Status); Assert.Empty(await db.CustomerStatusHistory.ToListAsync()); return true; });
        }
        finally { await Scope(async sp => { await sp.GetRequiredService<AuditDbContext>().Database.ExecuteSqlRawAsync("DROP FUNCTION audit.fail_insert() CASCADE;"); return true; }); }
    }
    [Fact]
    public async Task Report_row_limit_is_enforced_without_publishing_partial_CSV()
    {
        var u = await UserAsync("report.generate", "report.view", "customer.view"); await Scope(async sp => { var db = sp.GetRequiredService<CustomersDbContext>(); var at = DateTimeOffset.UtcNow.AddMinutes(-1); db.Customers.AddRange(Enumerable.Range(0, 10001).Select(i => new Customer { IdentityUserId = Guid.NewGuid(), FirstName = "Amina", LastName = "Private", CreatedAt = at })); await db.SaveChangesAsync(); return true; });
        var j = await Create(u, "oversized"); await Consume(j.Id); var r = await Read<ReportReceipt>(await u.Client.GetAsync($"/api/v1/admin/reports/{j.Id}")); Assert.Equal("Failed", r.Status); Assert.Equal("REPORT_ROW_LIMIT_EXCEEDED", r.FailureCode);
        Assert.Null(await Scope(sp => sp.GetRequiredService<IReportFileStore>().ReadAsync(j.Id, default)));
    }
    [Fact]
    public async Task Creating_and_changing_a_role_uses_concurrency_revokes_affected_sessions_and_audits()
    {
        var u = await UserAsync("role.manage", "permission.assign", "staff.manage", "customer.view"); await Mfa(u);
        var role = await Read<AdminRole>(await u.Client.PostAsJsonAsync("/api/v1/admin/roles", new RoleCreate("Support", "Support", ["customer.view"], "Review")));
        var target = await UserAsync(); var version = await Scope(sp => sp.GetRequiredService<IdentityDbContext>().Users.Where(x => x.Id == target.Id).Select(x => x.AuthorizationVersion).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await u.Client.PutAsJsonAsync($"/api/v1/admin/staff/{target.Id}/roles", new StaffRoleChange([role.Id], version, "Review"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/admin/customers/search")).StatusCode);
        var change = new RoleChange("Support updated", ["customer.view"], role.ConcurrencyVersion, "Review"); Assert.Equal(HttpStatusCode.OK, (await u.Client.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}", change)).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await u.Client.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}", change)).StatusCode);
        Assert.True(await Scope(sp => sp.GetRequiredService<AuditDbContext>().Logs.AnyAsync(x => x.Action == "role.change" && x.Result == "Success")));
    }
    [Fact]
    public async Task Forged_report_events_cannot_claim_jobs_or_expand_their_specification()
    {
        var u = await UserAsync("report.generate", "customer.view"); var j = await Create(u, "forged");
        await Scope(async sp => { var db = sp.GetRequiredService<ReportingDbContext>(); var row = await db.Jobs.SingleAsync(x => x.Id == j.Id); var envelope = WalletEvents.Report(row.Id, row.CreatedAt, row.CorrelationId) with { EventId = Guid.NewGuid() }; await Assert.ThrowsAsync<InvalidEventException>(() => sp.GetRequiredService<ReportConsumer>().ConsumeAsync(envelope, default)); Assert.Equal("Queued", (await db.Jobs.AsNoTracking().SingleAsync()).Status); return true; });
    }
    [Fact]
    public async Task Phase10_migrations_and_model_snapshots_match()
    {
        await factory.Services.InitializeAuditModuleAsync(); await factory.Services.InitializeAdministrationModuleAsync(); await factory.Services.InitializeReportingModuleAsync();
        await Scope(async sp => { foreach (var db in new DbContext[] { sp.GetRequiredService<AuditDbContext>(), sp.GetRequiredService<AdministrationDbContext>(), sp.GetRequiredService<ReportingDbContext>() }) { Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync()); } return true; });
    }
}
