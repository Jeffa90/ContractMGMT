using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Ecp.Operations;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>OPS-03 rules that do not need a database: how statuses combine, that the readiness catalogue is the specification's, the support-bundle limits, and the shape of the operational telemetry.</summary>
public sealed class OperationsDashboardTests
{
    [Fact]
    public void Failed_wins_then_not_verified_then_warning_then_verified_and_nothing_at_all_is_not_verified()
    {
        Assert.Equal(OpsStatus.NotVerified, OpsStatuses.Worst([]));
        Assert.Equal(OpsStatus.Verified, OpsStatuses.Worst([OpsStatus.Verified, OpsStatus.Verified]));
        Assert.Equal(OpsStatus.Warning, OpsStatuses.Worst([OpsStatus.Verified, OpsStatus.Warning]));
        Assert.Equal(OpsStatus.NotVerified, OpsStatuses.Worst([OpsStatus.Verified, OpsStatus.NotVerified]));
        Assert.Equal(OpsStatus.NotVerified, OpsStatuses.Worst([OpsStatus.Warning, OpsStatus.NotVerified, OpsStatus.Verified]));
        Assert.Equal(OpsStatus.Failed, OpsStatuses.Worst([OpsStatus.NotVerified, OpsStatus.Warning, OpsStatus.Failed, OpsStatus.Verified]));
        // Order never matters.
        var all = new[] { OpsStatus.Verified, OpsStatus.Warning, OpsStatus.NotVerified, OpsStatus.Failed };
        foreach (var rotated in Enumerable.Range(0, 4).Select(i => all.Skip(i).Concat(all.Take(i)))) Assert.Equal(OpsStatus.Failed, OpsStatuses.Worst(rotated));
    }

    [Fact]
    public void The_four_states_have_four_different_words_and_only_verified_looks_like_a_pass()
    {
        var states = Enum.GetValues<OpsStatus>();
        Assert.Equal(4, states.Select(OpsStatuses.Label).Distinct().Count());
        Assert.Equal(4, states.Select(OpsStatuses.Css).Distinct().Count());
        Assert.Equal("Not Verified", OpsStatuses.Label(OpsStatus.NotVerified));
        Assert.NotEqual(OpsStatuses.Css(OpsStatus.Verified), OpsStatuses.Css(OpsStatus.NotVerified));
        Assert.Equal("ok", OpsStatuses.Css(OpsStatus.Verified));
    }

    [Fact]
    public void The_readiness_catalogue_is_exactly_the_one_in_the_specification()
    {
        var spec = File.ReadAllLines(Path.Combine(RepoRoot.Find(), "requirements", "12-operations", "SETUP_AND_READINESS_SPEC.md"));
        var rows = spec.Select(l => Regex.Match(l, @"^\| (R-[A-Z]+-[0-9A-Z]+) \| (.+?) \| ([CWI](?: / [CW])?) \| (Yes)? ?\|\s*$")).Where(m => m.Success).ToList();
        Assert.Equal(rows.Select(m => m.Groups[1].Value), ReadinessCatalogue.Entries.Select(e => e.Id));
        Assert.Equal(rows.Select(m => m.Groups[3].Value), ReadinessCatalogue.Entries.Select(e => e.Severity));
        Assert.Equal(rows.Select(m => m.Groups[4].Value == "Yes"), ReadinessCatalogue.Entries.Select(e => e.Startup));
        Assert.Equal(ReadinessCatalogue.Entries.Count, ReadinessCatalogue.Entries.Select(e => e.Id).Distinct().Count());
        Assert.All(ReadinessCatalogue.RunElsewhere, id => Assert.Contains(ReadinessCatalogue.Entries, e => e.Id == id));
    }

    [Theory]
    [InlineData("bundle.json", true)] [InlineData("manifest.json", true)] [InlineData("summary.csv", true)] [InlineData("README.txt", true)]
    [InlineData("../bundle.json", false)] [InlineData("a/b.json", false)] [InlineData("a\\b.json", false)] [InlineData("/etc/passwd", false)] [InlineData(".hidden", false)]
    [InlineData("..", false)] [InlineData("", false)] [InlineData("a..b", false)] [InlineData("name with space.json", false)] [InlineData("C:evil.json", false)] [InlineData("bundle.json\0", false)]
    public void Support_bundle_entry_names_are_plain_file_names_from_a_short_allow_list(string name, bool safe) => Assert.Equal(safe, SupportBundleRules.SafeEntryName(name));

    [Fact]
    public void Support_bundle_limits_are_small_and_fixed()
    {
        Assert.True(SupportBundleRules.MaxEntryBytes <= 2 * 1024 * 1024);
        Assert.True(SupportBundleRules.MaxTotalBytes <= 5L * 1024 * 1024);
        Assert.False(SupportBundleRules.SafeEntryName(new string('a', 41)));
    }

    [Fact]
    public void Gauges_are_read_back_in_a_fixed_order_and_metric_tags_are_fixed_words()
    {
        OperationalTelemetry.SetGauge("outbox", "Pending", 3); OperationalTelemetry.SetGauge("outbox", "DeadLetter", 1);
        var snapshot = OperationalTelemetry.GaugeSnapshot();
        Assert.Equal(3, snapshot["outbox.Pending"]); Assert.Equal(1, snapshot["outbox.DeadLetter"]);
        Assert.Equal(snapshot.Keys.Order(StringComparer.Ordinal), snapshot.Keys);

        var observed = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == OperationalTelemetry.MeterName) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => observed.Add(i.Name + ":" + string.Join(",", tags.ToArray().Select(t => t.Key + "=" + t.Value))));
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.Contains("ecp.outbox.items:state=Pending", observed);
        Assert.Contains("ecp.outbox.items:state=DeadLetter", observed);
    }

    [Fact]
    public void An_operation_event_counts_once_by_kind_and_status_and_logs_failures_as_warnings_with_the_correlation_id_only()
    {
        var seen = new List<(string Name, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "ecp.operations.finished") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => seen.Add((i.Name, string.Join(",", tags.ToArray().Select(t => t.Key + "=" + t.Value)))));
        listener.Start();
        var log = new CapturingLogger();
        OperationalLog.OperationFinished(log, "Backup", "Completed", "corr-1");
        OperationalLog.OperationFinished(log, "Restore", "Failed", "corr-2");
        Assert.Contains(("ecp.operations.finished", "kind=Backup,status=Completed"), seen);
        Assert.Contains(("ecp.operations.finished", "kind=Restore,status=Failed"), seen);
        Assert.Equal([(LogLevel.Information, "Operation Backup finished: Completed (correlation corr-1)."), (LogLevel.Warning, "Operation Restore finished: Failed (correlation corr-2).")], log.Lines);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel, string)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void The_dashboard_export_carries_what_the_page_shows_and_nothing_more()
    {
        var item = new DashboardItem("X-1", "Title", OpsStatus.NotVerified, "Not looked at.", "detail text", null, "corr-9", "Do this.");
        var panel = new DashboardPanel("p", "P", "summary", OpsStatuses.Worst([item.Status]), [item]);
        var d = new OperationsDashboard(DateTime.UtcNow, "corr-0", OpsStatus.NotVerified, "1.1.0", "Test", "SQLite", MaintenanceStatus.Off, [panel], [], new CatalogueCoverage(30, 1, 2, 27), [], [], false, new Dictionary<string, long>());
        var json = System.Text.Json.JsonSerializer.Serialize(DashboardExport.Of(d));
        Assert.Contains("\"Not Verified\"", json);
        Assert.DoesNotContain("\"Verified\",\"items\"", json);
        Assert.Contains("corr-9", json);
    }
}
