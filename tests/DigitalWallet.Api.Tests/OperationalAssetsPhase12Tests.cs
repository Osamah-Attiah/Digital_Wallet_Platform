using System.Text.Json;
using Xunit;
namespace DigitalWallet.Api.Tests;
public sealed class OperationalAssetsPhase12Tests
{
    private static string Root()
    { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Digital_Wallet_Platform.sln"))) return d.FullName; throw new DirectoryNotFoundException(); }
    [Fact]
    public void Phase12_dashboards_have_valid_panels_units_descriptions_and_accessible_legends()
    {
        var paths = Directory.GetFiles(Path.Combine(Root(), "ops/observability/dashboards"), "*.json"); Assert.Equal(5, paths.Length);
        foreach (var path in paths)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var d = doc.RootElement;
            Assert.Equal("utc", d.GetProperty("timezone").GetString()); Assert.True(d.GetProperty("schemaVersion").GetInt32() >= 39);
            Assert.NotEmpty(d.GetProperty("panels").EnumerateArray());
            var ids = new HashSet<int>();
            foreach (var p in d.GetProperty("panels").EnumerateArray())
            {
                Assert.True(ids.Add(p.GetProperty("id").GetInt32()));
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("unit").GetString()));
                Assert.True(p.GetProperty("options").GetProperty("legend").GetProperty("showLegend").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("targets")[0].GetProperty("expr").GetString()));
                var pos = p.GetProperty("gridPos"); Assert.InRange(pos.GetProperty("x").GetInt32() + pos.GetProperty("w").GetInt32(), 1, 24);
                Assert.DoesNotContain("UserId", p.ToString()); Assert.DoesNotContain("WalletId", p.ToString());
            }
        }
    }
    [Fact]
    public void Phase12_alert_rules_have_windows_severity_and_existing_runbooks()
    {
        var ops = Path.Combine(Root(), "ops/observability"); using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ops, "alerts.rules.json")));
        var rules = doc.RootElement.GetProperty("groups")[0].GetProperty("rules").EnumerateArray().ToArray(); Assert.Equal(19, rules.Length);
        Assert.Equal(rules.Length, rules.Select(x => x.GetProperty("alert").GetString()).Distinct().Count());
        foreach (var r in rules)
        {
            Assert.Matches("^[0-9]+[ms]$", r.GetProperty("for").GetString()!);
            Assert.Contains(r.GetProperty("labels").GetProperty("severity").GetString(), new[] { "warning", "critical" });
            var link = r.GetProperty("annotations").GetProperty("runbook_url").GetString()!;
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(ops, link))));
        }
        Assert.Equal("warning", rules.Single(x => x.GetProperty("alert").GetString() == "RedisUnavailable").GetProperty("labels").GetProperty("severity").GetString());
        Assert.Equal("0m", rules.Single(x => x.GetProperty("alert").GetString() == "LedgerImbalance").GetProperty("for").GetString());
    }
    [Fact]
    public void Phase12_CSP_is_compatible_with_the_built_React_entrypoint_and_has_no_unsafe_inline_scripts()
    {
        var admin = Path.Combine(Root(), "apps/admin"); using var headers = JsonDocument.Parse(File.ReadAllText(Path.Combine(admin, "security-headers.json")));
        var csp = headers.RootElement.GetProperty("Content-Security-Policy").GetString()!;
        Assert.Contains("script-src 'self'", csp); Assert.Contains("frame-ancestors 'none'", csp); Assert.DoesNotContain("unsafe-inline", csp); Assert.DoesNotContain("unsafe-eval", csp);
        var index = File.ReadAllText(Path.Combine(admin, "index.html"));
        Assert.Contains("src=", index); Assert.DoesNotContain("<style", index); Assert.DoesNotContain("javascript:", index);
    }
}
