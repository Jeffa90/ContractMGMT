using System.Text.RegularExpressions;
using Ecp.Contracts.Register;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using static Ecp.Integration.Tests.CustomFieldWorld;

namespace Ecp.Integration.Tests;

/// <summary>Collects the SQL that Entity Framework sends, so a scenario can assert the shape of the statements a search produces.</summary>
internal sealed class CommandRecorder : ILoggerProvider
{
    private readonly List<string> sql = [];
    private readonly object gate = new();
    public bool Recording { get; set; }

    public ILogger CreateLogger(string categoryName) => categoryName == "Microsoft.EntityFrameworkCore.Database.Command" ? new Sink(this) : NullLogger();
    public void Dispose() { }
    private static ILogger NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IReadOnlyList<string> Take() { lock (gate) { var copy = sql.ToList(); sql.Clear(); return copy; } }

    private sealed class Sink(CommandRecorder owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.Recording && logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!owner.Recording) return;
            var text = formatter(state, exception);
            var at = text.IndexOf('\n');
            lock (owner.gate) owner.sql.Add(at < 0 ? text : text[(at + 1)..]);
        }
    }
}

/// <summary>
/// PER-002 regression guards for the register. They assert the *shape* of what a search sends to the database (how often a table is read, how many statements, whether the count grows with the rows), never a
/// time: a time depends on the machine, a shape does not. The shapes are the ones docs/PERFORMANCE_REVIEW.md found to cost the most on SQL Server.
/// </summary>
internal static class SearchPlanShapeScenarios
{
    internal static Action<IServiceCollection> Record(CommandRecorder recorder) => services => services.AddLogging(b => b.AddProvider(recorder).SetMinimumLevel(LogLevel.Information));

    private static int TableReads(string sql, string table) => Regex.Matches(sql, "(FROM|JOIN)\\s+[\"\\[`]?(\\w+[\"\\]`]?\\.[\"\\[`]?)?" + table + "[\"\\]`]?(?![\\w])", RegexOptions.IgnoreCase).Count;

    private static async Task<LifecycleWorld> WorldWithFieldsAsync(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.PublishFieldsAsync(Doc(string.Join(",",
            Field("memo", "TEXT", "\"searchable\": true, \"filterable\": true, \"sortable\": true"),
            Field("score", "INTEGER", "\"filterable\": true, \"sortable\": true"),
            Field("renewal_on", "DATE", "\"filterable\": true, \"sortable\": true"),
            Field("secret", "TEXT", "\"classification\": \"Restricted\", \"searchable\": true, \"filterable\": true, \"sortable\": true"))));
        for (var i = 0; i < 6; i++)
            await RegisterScenarios.Make(w, $"Shape {i}", custom: new() { ["memo"] = $"note {i}", ["score"] = (i * 10).ToString(), ["renewal_on"] = "2030-01-0" + (i + 1) });
        return w;
    }

    public static async Task A_search_whose_fields_every_grant_may_read_does_not_repeat_an_access_subquery_over_the_contracts(TestApp app, CommandRecorder recorder)
    {
        var w = await WorldWithFieldsAsync(app);
        recorder.Recording = true;
        // Free text across a searchable configured field, a configured-field filter and a configured-field sort, for a person whose every grant reads the field's level.
        var q = new ContractQuery(Text: "note 3", Filters: [new FilterClause("score", FilterOps.Between, ["0", "50"]), new FilterClause("renewal_on", FilterOps.Lte, ["2031-01-01"])], Sorts: [new SortClause("score", true)],
            Columns: ["agreementReference", "memo", "score"], OrganisationId: w.W.Org1);
        var page = await w.As("procAdmin", s => s.Get<IContractRegister>().SearchAsync(q));
        var statements = recorder.Take();
        recorder.Recording = false;
        Assert.Contains(page.Rows, r => r.Title == "Shape 3");
        var search = statements.Where(x => x.Contains("ContractCustomValue", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(search);
        foreach (var x in search.Where(x => x.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) || x.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)))
        {
            // The contracts table is read once for the page of contracts, plus once for the access test of the text-search values that need a state limit. Before PER-002 each configured-field condition, each text
            // word and the sort also read it for the access test, and this query read it seven times.
            var reads = TableReads(x, "Contract");
            Assert.True(reads <= 2, $"The search reads the Contract table {reads} times (at most 2 expected); a per-condition access subquery came back:\n{x}");
        }
    }

    public static async Task The_number_of_statements_for_a_page_does_not_grow_with_the_rows_on_it(TestApp app, CommandRecorder recorder)
    {
        var w = await WorldWithFieldsAsync(app);
        var seen = new Dictionary<int, string>();
        async Task<int> CountFor(int pageSize)
        {
            recorder.Recording = true;
            await w.As("procAdmin", s => s.Get<IContractRegister>().SearchAsync(new ContractQuery(Columns: ["agreementReference", "supplier", "category", "memo", "score", "renewal_on", "owner"], PageSize: pageSize, OrganisationId: w.W.Org1)));
            var all = recorder.Take().Where(x => x.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
            recorder.Recording = false;
            seen[pageSize] = string.Join("\n---\n", all.Select(x => x.Length > 160 ? x[..160] : x));
            return all.Count;
        }
        await CountFor(2);                          // the first call also loads configuration into caches
        var few = await CountFor(2);
        var many = await CountFor(50);
        Assert.True(many <= few, $"A page of 50 rows took {many} SELECT statements and a page of 2 took {few}: the statements grow with the rows (N+1).");
        // S6: the capability resolver adds a bounded number of reads per request (release, organisation and pack assignment, once per organisation in reach; the pack document is cached), so the ceiling moved from 40 to 42. S10: the register resolves the organisation's regional profile once per request to decide whether the tax identifier may be read (one release read, memoised for the rest of the request), so the ceiling moved to 44.
        Assert.True(many <= 44, $"A page took {many} SELECT statements (34 before S6; the identity, configuration, tenancy and capability lookups are about half of them):\n{seen[50]}");
    }
}

public sealed class SearchPlanShapeTests_Sqlite
{
    private static Task Run(Func<TestApp, CommandRecorder, Task> scenario)
    {
        var recorder = new CommandRecorder();
        return ProviderRunner.SqliteAsync(app => scenario(app, recorder), SearchPlanShapeScenarios.Record(recorder));
    }

    [Fact] public Task Open_fields_skip_the_access_subquery() => Run(SearchPlanShapeScenarios.A_search_whose_fields_every_grant_may_read_does_not_repeat_an_access_subquery_over_the_contracts);
    [Fact] public Task Statements_do_not_grow_with_rows() => Run(SearchPlanShapeScenarios.The_number_of_statements_for_a_page_does_not_grow_with_the_rows_on_it);
}

[Trait("Category", "SqlServer")]
public sealed class SearchPlanShapeTests_SqlServer
{
    private static Task Run(Func<TestApp, CommandRecorder, Task> scenario)
    {
        var recorder = new CommandRecorder();
        return ProviderRunner.SqlServerAsync(app => scenario(app, recorder), SearchPlanShapeScenarios.Record(recorder));
    }

    [SqlServerFact] public Task Open_fields_skip_the_access_subquery() => Run(SearchPlanShapeScenarios.A_search_whose_fields_every_grant_may_read_does_not_repeat_an_access_subquery_over_the_contracts);
    [SqlServerFact] public Task Statements_do_not_grow_with_rows() => Run(SearchPlanShapeScenarios.The_number_of_statements_for_a_page_does_not_grow_with_the_rows_on_it);
}
