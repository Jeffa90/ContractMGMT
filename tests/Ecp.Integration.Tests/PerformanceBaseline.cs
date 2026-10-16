using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Contracts.Register;
using Ecp.Metadata;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>
/// A measured baseline of the register on a synthetic tenant of at least 10,000 contracts: first page, filtered search, configured-field filter, saved-view load, detail load, export initiation and configuration
/// resolution. It records the environment next to the numbers and asserts no time limit: a baseline is evidence, not a service-level promise. The result is written to
/// <c>artifacts/performance/&lt;provider&gt;.json</c> for docs/PERFORMANCE.md.
/// </summary>
internal static class PerformanceBaseline
{
    public static int Contracts => int.TryParse(Environment.GetEnvironmentVariable("ECP_PERF_CONTRACTS"), out var n) && n >= 1000 ? n : 10_000;
    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("ECP_PERF_RUNS"), out var r) && r >= 3 ? r : 15;

    private sealed record Sample(string Operation, string User, int Runs, double MedianMs, double P95Ms, double MaxMs, int Rows, int Total, string Note);

    private static async Task<List<double>> TimeAsync(Func<Task> work, int? runCount = null)
    {
        var runs = runCount ?? Runs;
        await work();                                               // one unmeasured run so the first compile and connection are not counted
        var times = new List<double>();
        for (var i = 0; i < runs; i++) { var sw = Stopwatch.StartNew(); await work(); sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds); }
        times.Sort();
        return times;
    }

    private static Sample Summarise(string op, string user, List<double> t, int rows, int total, string note = "")
        => new(op, user, t.Count, Math.Round(t[t.Count / 2], 1), Math.Round(t[(int)Math.Ceiling(t.Count * 0.95) - 1], 1), Math.Round(t[^1], 1), rows, total, note);

    public static async Task Measure(TestApp app, string provider, ITestOutputHelper output)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = w.W.Org1;
        await w.PublishFieldsAsync(Doc(string.Join(",",
            Field("service_tier", "SINGLE_CHOICE", Choices + ", \"filterable\": true, \"sortable\": true, \"searchable\": true, \"reportable\": true"),
            Field("score", "INTEGER", "\"filterable\": true, \"sortable\": true, \"reportable\": true"),
            Field("memo", "TEXT", "\"searchable\": true, \"filterable\": true, \"reportable\": true"),
            Field("renewal_on", "DATE", "\"filterable\": true, \"sortable\": true"))));

        // ---- synthetic data: deterministic, no real names or amounts ----------------------------------------------------------------------------------
        var n = Contracts;
        var seedWatch = Stopwatch.StartNew();
        var rnd = new Random(20261016);
        var statuses = new[] { "Draft", "Effective", "Effective", "Effective", "Ended", "Suspended" };
        var types = new[] { "Goods", "Services", "Licence", "Lease" };
        var risks = new[] { "Low", "Medium", "High" };
        var tiers = new[] { "GOLD", "SILVER", "BRONZE" };
        var today = w.Today;
        int suppliers = 40;
        var supplierIds = new List<Guid>();
        await using (var s = w.W.AsScopeless())
        {
            var sup = s.Get<ISupplierService>();
            for (var i = 0; i < suppliers; i++) supplierIds.Add(await sup.CreateAsync(new CreateSupplierRequest(org, $"Synthetic Supplier {i:00} Pty Ltd", VendorNumber: $"PERF{i:000}", SupplierType: i % 5 == 0 ? "STRATEGIC" : "STANDARD")));
        }
        await using (var s = w.W.AsScopeless())
        {
            var db = s.Get<ContractsDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var tenant = w.W.TenantId; var now = app.Clock.UtcNow;
            for (var from = 0; from < n; from += 500)
            {
                for (var i = from; i < Math.Min(n, from + 500); i++)
                {
                    var start = today.AddDays(-rnd.Next(0, 800));
                    var c = Contract.CreateDraft(new NewContract(tenant, org, $"PERF-{i:000000}", $"Synthetic agreement {i}", supplierIds[i % suppliers], i % 3 == 0 ? w.W.CatPackaging : w.W.CatFlexible,
                        statuses[i % statuses.Length], "perf", now, null, "Internal", "AUD", 500 + (i * 37 % 250_000), null, i % 7 == 0, start, null, today.AddDays(rnd.Next(-30, 400)),
                        start.AddDays(rnd.Next(60, 2000)), w.W.Bu1, w.W.Site1, null, types[i % types.Length], null, risks[i % risks.Length], null, null, 3, "Months", i % 11 == 0));
                    db.Contracts.Add(c);
                    db.CustomValues.Add(ContractCustomValue.Create(tenant, org, c.Id, "service_tier", 0, 1, "perf", now, text: tiers[i % tiers.Length]));
                    db.CustomValues.Add(ContractCustomValue.Create(tenant, org, c.Id, "score", 0, 1, "perf", now, number: i % 101));
                    db.CustomValues.Add(ContractCustomValue.Create(tenant, org, c.Id, "memo", 0, 1, "perf", now, text: $"note {i % 997}"));
                    if (i % 2 == 0) db.CustomValues.Add(ContractCustomValue.Create(tenant, org, c.Id, "renewal_on", 0, 1, "perf", now, date: today.AddDays(rnd.Next(-100, 700))));
                }
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
        seedWatch.Stop();
        var count = await w.W.AsScopeless().Run(s => s.Get<ContractsDbContext>().Contracts.CountAsync(c => c.CreatedBy == "perf"));
        Assert.Equal(n, count);

        // ---- measurements ------------------------------------------------------------------------------------------------------------------------------
        var samples = new List<Sample>();
        var operations = new Dictionary<string, (string User, Func<TenantScope, Task<(int Rows, int Total)>> Work)>(StringComparer.Ordinal);
        async Task Run(string op, string user, Func<TenantScope, Task<(int Rows, int Total)>> f, string note = "")
        {
            operations.TryAdd(op, (user, f));
            (int Rows, int Total) last = default;
            var t = await TimeAsync(async () => last = await w.As(user, f));
            samples.Add(Summarise(op, user, t, last.Rows, last.Total, note));
        }

        await Run("Register first page (All Contracts, 25 rows)", "procAdmin", async s => { var p = await s.Get<IContractViews>().RunAsync("system:all-contracts", org); return (p.Rows.Count, p.Total); });
        await Run("Register first page (All Contracts, 25 rows)", "ownerPackaging", async s => { var p = await s.Get<IContractViews>().RunAsync("system:all-contracts", org); return (p.Rows.Count, p.Total); }, "limited to the owner's categories");
        await Run("Register page 40 (offset paging)", "procAdmin", async s => { var p = await s.Get<IContractViews>().RunAsync("system:all-contracts", org, 40); return (p.Rows.Count, p.Total); });
        await Run("Filtered search (core fields: value range, type, risk, supplier text)", "procAdmin", async s =>
        {
            var p = await s.Get<IContractRegister>().SearchAsync(new ContractQuery(Text: "synthetic supplier 07", Filters: [new FilterClause("estimatedValue", FilterOps.Between, ["10000", "150000"]), new FilterClause("contractType", FilterOps.Eq, ["Services"]), new FilterClause("riskLevel", FilterOps.In, ["High", "Medium"])],
                Sorts: [new SortClause("estimatedValue", true)], OrganisationId: org));
            return (p.Rows.Count, p.Total);
        });
        await Run("Configured-field filter (tier + score range + date, sorted by score)", "procAdmin", async s =>
        {
            var p = await s.Get<IContractRegister>().SearchAsync(new ContractQuery(Filters: [new FilterClause("service_tier", FilterOps.In, ["GOLD"]), new FilterClause("score", FilterOps.Between, ["10", "60"]), new FilterClause("renewal_on", FilterOps.Lte, ["@today+90"])],
                Sorts: [new SortClause("score", true)], Columns: ["agreementReference", "contractTitle", "service_tier", "score"], OrganisationId: org));
            return (p.Rows.Count, p.Total);
        });
        if (Environment.GetEnvironmentVariable("ECP_PERF_DIAG") == "1")
        {
            Task<(int, int)> Q(IContractRegister r, ContractQuery q) => r.SearchAsync(q).ContinueWith(t => (t.Result.Rows.Count, t.Result.Total));
            await Run("DIAG tier filter only", "procAdmin", s => Q(s.Get<IContractRegister>(), new ContractQuery(Filters: [new FilterClause("service_tier", FilterOps.In, ["GOLD"])], OrganisationId: org)));
            await Run("DIAG score filter only", "procAdmin", s => Q(s.Get<IContractRegister>(), new ContractQuery(Filters: [new FilterClause("score", FilterOps.Between, ["10", "60"])], OrganisationId: org)));
            await Run("DIAG date filter only", "procAdmin", s => Q(s.Get<IContractRegister>(), new ContractQuery(Filters: [new FilterClause("renewal_on", FilterOps.Lte, ["@today+90"])], OrganisationId: org)));
            await Run("DIAG sort by score only", "procAdmin", s => Q(s.Get<IContractRegister>(), new ContractQuery(Sorts: [new SortClause("score", true)], OrganisationId: org)));
            await Run("DIAG sort by core only", "procAdmin", s => Q(s.Get<IContractRegister>(), new ContractQuery(Sorts: [new SortClause("estimatedValue", true)], OrganisationId: org)));
        }
        await Run("Configured-field text search (searchable field)", "procAdmin", async s =>
        {
            var p = await s.Get<IContractRegister>().SearchAsync(new ContractQuery(Text: "note 42", OrganisationId: org));
            return (p.Rows.Count, p.Total);
        });
        string savedRef = "";
        await w.As("procAdmin", async s =>
        {
            savedRef = await s.Get<IContractViews>().SaveMineAsync(null, new ViewDefinition("mine", "Perf view", "", ["agreementReference", "contractTitle", "supplier", "service_tier", "score", "endDate"], [new SortClause("endDate")],
                [new FilterClause("service_tier", FilterOps.In, ["SILVER", "GOLD"])], 50));
            return 0;
        });
        await Run("Saved view load (personal view, configured columns and filter)", "procAdmin", async s => { var p = await s.Get<IContractViews>().RunAsync(savedRef, org); return (p.Rows.Count, p.Total); });
        await Run("Shipped view: Expiring Soon", "procAdmin", async s => { var p = await s.Get<IContractViews>().RunAsync("system:expiring-soon", org); return (p.Rows.Count, p.Total); });

        var oneId = await w.W.AsScopeless().Run(s => s.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.CreatedBy == "perf" && c.CategoryId == w.W.CatPackaging).OrderBy(c => c.AgreementReference).Select(c => c.Id).Skip(1234).FirstAsync());
        await Run("Contract detail load (core details + configured fields)", "ownerPackaging", async s =>
        {
            var d = await s.Get<ISecuredContractService>().GetAsync(oneId);
            var f = await s.Get<IContractCustomFields>().ForContractAsync(oneId);
            return (f.Fields.Count, d.Title.Length);
        });
        await Run("Export initiation (CSV of a 500-row search)", "procAdmin", async s =>
        {
            var r = await s.Get<IContractExports>().ExportAsync(new ExportRequest(new ContractQuery(Filters: [new FilterClause("service_tier", FilterOps.Eq, ["BRONZE"]), new FilterClause("score", FilterOps.Between, ["0", "10"]), new FilterClause("riskLevel", FilterOps.Eq, ["High"])], Columns: ["agreementReference", "contractTitle", "supplier", "estimatedValue", "service_tier", "score"], OrganisationId: org)));
            return (r.Rows, r.TotalMatching);
        });

        // Configuration resolution: the published definitions with a warm cache, and after an invalidation (a cold read through the configuration tables).
        var catalogueWarm = await TimeAsync(async () => { await using var sc = w.W.AsScopeless(); await sc.Get<ICustomFieldCatalogue>().PublishedAsync(org); }, 50);
        samples.Add(Summarise("Configuration resolution, warm cache (custom field definitions)", "system", catalogueWarm, 4, 4));
        var cache = app.Services.GetRequiredService<IPublishedConfigurationCache>();
        var cold = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            cache.Invalidate(w.W.TenantId, org, MetadataModule.CustomFieldKind);
            var sw = Stopwatch.StartNew();
            await using (var sc = w.W.AsScopeless()) await sc.Get<ICustomFieldCatalogue>().PublishedAsync(org);
            sw.Stop(); cold.Add(sw.Elapsed.TotalMilliseconds);
        }
        cold.Sort();
        samples.Add(Summarise("Configuration resolution, cold (after invalidation)", "system", cold, 4, 4));

        // ---- PER-002 evidence (SQL Server only): the statements, plans and parameter sensitivity of the configured-field operations --------------------------------
        if (provider == "sqlserver") await WritePlanReportAsync(app, w, org, operations, output);

        // ---- record ------------------------------------------------------------------------------------------------------------------------------------
        var environment = new
        {
            provider, contracts = n, customValues = await w.W.AsScopeless().Run(s => s.Get<ContractsDbContext>().CustomValues.CountAsync()), seedSeconds = Math.Round(seedWatch.Elapsed.TotalSeconds, 1),
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(), logicalCores = Environment.ProcessorCount,
            gcServer = System.Runtime.GCSettings.IsServerGC, build = typeof(PerformanceBaseline).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).Length > 0 ? "as built by the test run" : "release",
            measuredUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), runsPerOperation = Runs,
            note = "Latencies are in milliseconds for the application services in-process (no HTTP, no network), median and 95th percentile of the runs after one unmeasured run. The synthetic tenant has no real data."
        };
        var dir = Path.Combine(FindRepoRoot(), "artifacts", "performance"); Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new { environment, samples }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(dir, provider + ".json"), json);
        output.WriteLine(json);
        // A baseline records; it does not promise. The one thing asserted is the shape of the cost: a configured-field search must not cost orders of magnitude more than a core-field search over the same rows,
        // which is what a lookup per contract (instead of one join) did before it was found by this measurement.
        var core = samples.First(x => x.Operation.StartsWith("Filtered search", StringComparison.Ordinal)).MedianMs;
        var configured = samples.First(x => x.Operation.StartsWith("Configured-field filter", StringComparison.Ordinal)).MedianMs;
        var export = samples.First(x => x.Operation.StartsWith("Export initiation", StringComparison.Ordinal)).MedianMs;
        Assert.True(configured < Math.Max(5000, 50 * core), $"A configured-field filter took {configured} ms against {core} ms for a core-field search.");
        Assert.True(export < Math.Max(5000, 50 * core), $"A 500-row export took {export} ms against {core} ms for a core-field search.");
    }

    private static async Task WritePlanReportAsync(TestApp app, LifecycleWorld w, Guid org, Dictionary<string, (string User, Func<TenantScope, Task<(int Rows, int Total)>> Work)> operations, ITestOutputHelper output)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            await QueryPlanReport.EnableAsync(app.ConnectionString);
            foreach (var name in new[]
            {
                "Configured-field text search (searchable field)", "Configured-field filter (tier + score range + date, sorted by score)",
                "Saved view load (personal view, configured columns and filter)", "Export initiation (CSV of a 500-row search)", "Filtered search (core fields: value range, type, risk, supplier text)"
            })
            {
                if (!operations.TryGetValue(name, out var op)) continue;
                report.AppendLine(await QueryPlanReport.CaptureAsync(app.ConnectionString, name, () => w.As(op.User, op.Work)));
            }

            // Parameter sensitivity: the same statement shape compiled for a rare value and then used for a common one, and the reverse, each timed once cold and twice warm.
            async Task<string> Probe(string label, Func<string, Task> run, string first, string second)
            {
                await QueryPlanReport.ForgetPlansAsync(app.ConnectionString);
                var t = new List<string>();
                foreach (var v in new[] { first, second, second, first })
                {
                    var sw = Stopwatch.StartNew(); await run(v); sw.Stop();
                    t.Add($"{v}={sw.Elapsed.TotalMilliseconds:0.0}ms");
                }
                return $"{label}: compiled for '{first}' then {string.Join(", ", t)}";
            }
            Task Text(string v) => w.As("procAdmin", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(Text: v, OrganisationId: org)));
            Task Score(string v) => w.As("procAdmin", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(Filters: [new FilterClause("score", FilterOps.Between, [v, v])], Sorts: [new SortClause("score", true)], OrganisationId: org)));
            report.AppendLine("=== parameter sensitivity");
            report.AppendLine(await Probe("text search, rare first", Text, "note 4242", "note"));
            report.AppendLine(await Probe("text search, common first", Text, "note", "note 4242"));
            report.AppendLine(await Probe("score filter, rare first", Score, "100", "50"));
            report.AppendLine(await Probe("score filter, common first", Score, "50", "100"));
        }
        catch (Exception e) { report.AppendLine("PLAN REPORT FAILED: " + e); }
        var dir = Path.Combine(FindRepoRoot(), "artifacts", "performance"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plans-sqlserver.txt"), report.ToString());
        output.WriteLine(report.ToString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

[Trait("Category", "Performance")]
public sealed class PerformanceBaselineTests_Sqlite(ITestOutputHelper output)
{
    [Fact] public Task Measure_the_register_on_ten_thousand_contracts() => ProviderRunner.SqliteAsync(app => PerformanceBaseline.Measure(app, "sqlite", output), LifecycleScenarios.Scanner);
}

[Trait("Category", "Performance")]
[Trait("Category", "SqlServer")]
public sealed class PerformanceBaselineTests_SqlServer(ITestOutputHelper output)
{
    [SqlServerFact] public Task Measure_the_register_on_ten_thousand_contracts() => ProviderRunner.SqlServerAsync(app => PerformanceBaseline.Measure(app, "sqlserver", output), LifecycleScenarios.Scanner);
}
