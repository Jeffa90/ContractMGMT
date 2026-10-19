using System.Net;
using System.Text.RegularExpressions;
using Ecp.Documents;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-25 over real HTTP: the Scanner page tells the truth about the scanner. A connected scanner is Not Verified until its self-test has passed with the current configuration; a scanner that says Clean to the standard test file
/// fails the test; the demonstration scanner is a prominent warning in Test and can be accepted only there, by the technical role; Demo has nothing to accept. No real scanner is used: the providers here are test doubles.
/// </summary>
public sealed class ScannerPageTests
{
    private const string SysAdmin = "sysadmin@installation.example.test";

    private sealed class DoubleScanner(bool detects, bool reachable = true) : IMalwareScanner
    {
        public string Name => "test double";
        public Task<ScannerHealth> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(new ScannerHealth(reachable, reachable ? "up" : "connection refused"));
        public async Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct);
            return detects && System.Text.Encoding.ASCII.GetString(ms.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE") ? ScanVerdict.Infected : ScanVerdict.Clean;
        }
    }

    private static DemoApp Start(string mode, IMalwareScanner? scanner, string? provider)
        => DemoApp.Start(s => { s["Ecp:Mode"] = mode; if (provider is not null) s["Documents:Scanner:Provider"] = provider; },
            scanner is null ? null : sc => sc.AddSingleton(scanner));

    private static string State(string html) => Regex.Match(html, "Readiness: <span class=\"badge [a-z]+\">([A-Za-z]+)</span>").Groups[1].Value;

    private static async Task<string> PageAsync(HttpClient c)
    {
        var r = await c.GetAsync("/Admin/Operations/Scanner");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string handler, Dictionary<string, string>? fields = null)
        => DemoApp.PostFormAsync(c, "/Admin/Operations/Scanner", fields ?? [], "/Admin/Operations/Scanner?handler=" + handler);

    [Fact]
    public async Task A_connected_scanner_is_not_verified_until_its_self_test_passes_and_a_ready_check_follows_it()
    {
        using var app = Start("Test", new DoubleScanner(detects: true), "test-double");
        using var sys = await app.SignInAsync(SysAdmin);
        var before = await PageAsync(sys);
        Assert.Equal("NotVerified", State(before));
        Assert.Contains("test double", before);

        Assert.Equal(HttpStatusCode.Redirect, (await Post(sys, "Test")).StatusCode);
        var after = await PageAsync(sys);
        Assert.Equal("Healthy", State(after));
        Assert.DoesNotContain("TEST MODE", after);                    // the demonstration warning belongs to the demonstration scanner only
    }

    [Fact]
    public async Task A_scanner_that_calls_the_standard_test_file_clean_fails_its_self_test_and_is_never_healthy()
    {
        using var app = Start("Test", new DoubleScanner(detects: false), "test-double");
        using var sys = await app.SignInAsync(SysAdmin);
        await Post(sys, "Test");
        Assert.Equal("Failed", State(await PageAsync(sys)));
    }

    [Fact]
    public async Task An_unreachable_scanner_fails_its_self_test()
    {
        using var app = Start("Test", new DoubleScanner(detects: true, reachable: false), "test-double");
        using var sys = await app.SignInAsync(SysAdmin);
        await Post(sys, "Test");
        Assert.Equal("Failed", State(await PageAsync(sys)));
    }

    [Fact]
    public async Task In_Test_the_demonstration_scanner_is_a_warning_that_only_the_system_administrator_can_accept_on_the_record()
    {
        using var app = Start("Test", null, "mock");
        using var sys = await app.SignInAsync(SysAdmin);
        var page = await PageAsync(sys);
        Assert.Equal("Demonstration", State(page));
        Assert.Contains("TEST MODE", page);

        // A reason is required; a reason makes the acceptance, audited and visible.
        var refused = await Post(sys, "Accept", new() { ["Scope"] = "masked-data rehearsal", ["Reason"] = "", ["ValidDays"] = "14" });
        Assert.Contains("error", (await refused.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.DoesNotContain("Accepted", Regex.Replace(await PageAsync(sys), "<[^>]+>", " ").Replace("Accepted by", ""));
        Assert.Equal(HttpStatusCode.Redirect, (await Post(sys, "Accept", new() { ["Scope"] = "masked-data rehearsal", ["Reason"] = "Rehearsal with masked data only", ["ValidDays"] = "14" })).StatusCode);
        Assert.Contains("masked-data rehearsal", await PageAsync(sys));

        // More than 90 days is refused.
        var tooLong = await Post(sys, "Accept", new() { ["Scope"] = "x", ["Reason"] = "y", ["ValidDays"] = "400" });
        Assert.NotEqual(HttpStatusCode.Redirect, tooLong.StatusCode);

        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/Admin/Operations/Scanner")).StatusCode);
    }

    [Fact]
    public async Task Demo_has_nothing_to_accept_and_refuses_an_acceptance()
    {
        using var app = Start("Demo", null, null);
        using var sys = await app.SignInAsync(SysAdmin);
        Assert.Equal("Demonstration", State(await PageAsync(sys)));
        var r = await Post(sys, "Accept", new() { ["Scope"] = "anything", ["Reason"] = "because", ["ValidDays"] = "14" });
        Assert.NotEqual(HttpStatusCode.Redirect, r.StatusCode);
    }

    [Fact]
    public async Task Test_with_no_provider_configured_reads_not_connected()
    {
        using var app = Start("Test", null, null);
        using var sys = await app.SignInAsync(SysAdmin);
        Assert.Equal("NotConnected", State(await PageAsync(sys)));
    }
}
