using Ecp.Audit;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>A scanner the test can switch between a working scanner, one that fails, one that is not connected and one that is down, so that the gate's behaviour in each is observed.</summary>
internal sealed class SwitchableScanner : IMalwareScanner
{
    public enum Behaviour { Clean, Fails, Errors, Disconnected }
    public volatile Behaviour Mode = Behaviour.Clean;
    public string Name => "test scanner";
    public bool IsConnected => Mode != Behaviour.Disconnected;
    public async Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default)
    {
        switch (Mode)
        {
            case Behaviour.Fails: throw new IOException("scanner unavailable");
            case Behaviour.Errors: return ScanVerdict.Error;
            case Behaviour.Disconnected: return ScanVerdict.Skipped;
        }
        using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct);
        return System.Text.Encoding.ASCII.GetString(ms.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE", StringComparison.Ordinal) ? ScanVerdict.Infected : ScanVerdict.Clean;
    }
}

/// <summary>
/// G-25: the malware gate. A file is released only when a connected scanner calls it clean; a scanner that fails, errors or is absent leaves the file held (never clean); a held file is retried a bounded number of times by a job
/// that is safe to repeat; an infected file stays in quarantine and nothing releases it; every outcome is recorded and audited; and an administrator sees only the organisations they administer.
/// </summary>
internal static class DocumentScanningScenarios
{
    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(HarborlineWorld w, string action)
    {
        await using var scope = w.AsScopeless();
        return await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == action).OrderBy(e => e.Sequence).ToListAsync();
    }

    private static Task<DocumentSummary?> DocumentAsync(HarborlineWorld w, Guid contract, string title)
        => w.AsAsync("admin", async s => (await s.Get<IDocumentQuery>().ListForContractAsync(contract)).FirstOrDefault(d => d.Title == title));

    private static async Task<Guid> AddAsync(HarborlineWorld w, Guid contract, string title, string content)
    {
        var dto = await w.AsAsync("packageManagerA", s => s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(w.Org, contract, "FinalAgreement", title, title + ".txt", "text/plain",
            new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)), "Internal")));
        return dto.VersionId;
    }

    public static async Task A_file_the_scanner_cannot_check_is_held_retried_a_bounded_number_of_times_and_released_only_by_a_clean_result(TestApp app, SwitchableScanner scanner)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var contract = await w.NewSubcontractAsync();
        scanner.Mode = SwitchableScanner.Behaviour.Fails;
        var version = await AddAsync(w, contract, "Held agreement", "ordinary text");

        var held = await DocumentAsync(w, contract, "Held agreement");
        Assert.NotNull(held);
        Assert.Equal(ScanStatus.Error, held!.ScanStatus);
        Assert.False(held.Downloadable);                                                       // a failure is never read as clean
        Assert.Null(await w.AsAsync("admin", s => s.Get<IDocumentQuery>().OpenAsync(held.DocumentId)));

        // The scheduled retry tries again, up to the limit, then stops and leaves it to an administrator.
        // The test clock stands still, so each step moves it on: attempts are then ordered by the time they were made.
        for (var i = 0; i < 4; i++) { app.Clock.Advance(TimeSpan.FromMinutes(1)); await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync()); }
        var item = await w.AsAsync("admin", async s => (await s.Get<IDocumentScanning>().HeldAsync()).Single(h => h.VersionId == version));
        Assert.Equal(3, item.Attempts);                                                        // the upload and two retries: the limit is 3 failures
        Assert.True(item.GivenUp);
        var last = await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync());
        Assert.Equal(0, last.Examined); Assert.Equal(1, last.GivenUp);
        Assert.Equal(2, (await AuditAsync(w, "document.scan.failed")).Count);

        // The scanner recovers: an administrator scans it again and it is released; the job and the page agree.
        scanner.Mode = SwitchableScanner.Behaviour.Clean;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ScanStatus.Clean, await w.AsAsync("admin", s => s.Get<IDocumentScanning>().RescanOneAsync(version)));
        Assert.True((await DocumentAsync(w, contract, "Held agreement"))!.Downloadable);
        var overview = await w.AsAsync("admin", s => s.Get<IDocumentScanning>().OverviewAsync());
        Assert.Equal(0, overview.Held);
        Assert.True(overview.ScannerConnected);
        Assert.Single(await AuditAsync(w, "document.scan.completed"));
        await using (var scope = w.AsScopeless())
        {
            var outcomes = await scope.Get<Ecp.Documents.Persistence.DocumentsDbContext>().ScanAttempts.AsNoTracking().Where(a => a.VersionId == version).OrderBy(a => a.AttemptedUtc).Select(a => new { a.Outcome, a.Trigger }).ToListAsync();
            Assert.Equal([(ScanStatus.Error, "upload"), (ScanStatus.Error, "rescan"), (ScanStatus.Error, "rescan"), (ScanStatus.Clean, "manual")], outcomes.Select(o => (o.Outcome, o.Trigger)));
        }

        // A job that runs again, or twice, changes nothing once the file is settled.
        var again = await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync());
        Assert.Equal(0, again.Examined);
        Assert.Equal(ScanStatus.Clean, await w.AsAsync("admin", s => s.Get<IDocumentScanning>().RescanOneAsync(version)));
        Assert.Single(await AuditAsync(w, "document.scan.completed"));
    }

    public static async Task An_infected_file_stays_in_quarantine_is_never_released_or_copied_and_is_audited(TestApp app, SwitchableScanner scanner)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var contract = await w.NewSubcontractAsync();
        var version = await AddAsync(w, contract, "Bad file", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE");
        var doc = await DocumentAsync(w, contract, "Bad file");
        Assert.Equal(ScanStatus.Infected, doc!.ScanStatus);
        Assert.False(doc.Downloadable);
        Assert.Null(await w.AsAsync("admin", s => s.Get<IDocumentQuery>().OpenAsync(doc.DocumentId)));
        var quarantined = Assert.Single(await AuditAsync(w, "document.quarantined"));
        Assert.Equal(version.ToString(), quarantined.EntityId);
        Assert.DoesNotContain("EICAR", quarantined.NewValuesJson ?? "");                          // no content in the audit trail

        // Neither the job nor an administrator releases it, and it cannot be copied to another contract.
        var run = await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync());
        Assert.Equal(0, run.Examined);
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IDocumentScanning>().RescanOneAsync(version)));
        Assert.Equal("quarantined", refused.Code);
        var other = await w.NewSubcontractAsync();
        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("packageManagerA", s => s.Get<IDocumentService>().CopyAsync(new CopyDocumentRequest(doc.DocumentId, other, w.Org, "Internal"))));
        Assert.Equal(ScanStatus.Infected, (await DocumentAsync(w, contract, "Bad file"))!.ScanStatus);
        var overview = await w.AsAsync("admin", s => s.Get<IDocumentScanning>().OverviewAsync());
        Assert.Equal(1, overview.Infected);
    }

    public static async Task Quarantine_scanner_trouble_and_exhausted_retries_alert_the_administrators_without_naming_the_file(TestApp app, SwitchableScanner scanner)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var contract = await w.NewSubcontractAsync();
        // The first pass only marks where the alerts start; events from before are history, not news.
        await w.AsAsync("admin", s => s.Get<Ecp.Notifications.INotificationDispatcher>().DispatchAsync());

        await AddAsync(w, contract, "Confidential supplier terms", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE");
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await w.AsAsync("admin", s => s.Get<Ecp.Notifications.INotificationDispatcher>().DispatchAsync());
        var quarantined = (await w.InboxAsync("admin")).Where(n => n.EventType == "scanning.quarantined").ToList();
        var alert = Assert.Single(quarantined);
        Assert.Equal("/Admin/Documents/Scanning", alert.LinkPath);
        Assert.DoesNotContain("Confidential supplier terms", alert.Title + alert.Body);          // the alert never names the file, the contract or the content
        Assert.DoesNotContain("EICAR", alert.Title + alert.Body);
        Assert.Contains(await w.InboxAsync("commercial"), n => n.EventType == "scanning.quarantined");   // the Procurement Administrator is told as well
        Assert.DoesNotContain(await w.InboxAsync("packageManagerA"), n => n.EventType.StartsWith("scanning."));   // and nobody else is

        // Running the dispatcher again makes no second alert for the same event.
        await w.AsAsync("admin", s => s.Get<Ecp.Notifications.INotificationDispatcher>().DispatchAsync());
        Assert.Single(await w.InboxAsync("admin"), n => n.EventType == "scanning.quarantined");

        // A scanner outage: the failures make one message per person per day, and a file given up on makes its own message.
        scanner.Mode = SwitchableScanner.Behaviour.Fails;
        await AddAsync(w, contract, "First held file", "ordinary text");
        await AddAsync(w, contract, "Second held file", "more ordinary text");
        for (var i = 0; i < 4; i++) { app.Clock.Advance(TimeSpan.FromMinutes(1)); await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync()); }
        await w.AsAsync("admin", s => s.Get<Ecp.Notifications.INotificationDispatcher>().DispatchAsync());
        var inbox = await w.InboxAsync("admin");
        Assert.Single(inbox, n => n.EventType == "scanning.provider_unavailable");
        Assert.Contains(inbox, n => n.EventType == "scanning.retry_exhausted");
        Assert.All(inbox.Where(n => n.EventType.StartsWith("scanning.")), n => Assert.DoesNotContain("held file", n.Title + n.Body));
    }

    public static async Task With_no_scanner_connected_every_file_is_held_and_connecting_one_releases_what_it_finds_clean(TestApp app, SwitchableScanner scanner)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var contract = await w.NewSubcontractAsync();
        scanner.Mode = SwitchableScanner.Behaviour.Disconnected;
        var version = await AddAsync(w, contract, "Waiting agreement", "ordinary text");
        var doc = await DocumentAsync(w, contract, "Waiting agreement");
        Assert.Equal(ScanStatus.Skipped, doc!.ScanStatus);
        Assert.False(doc.Downloadable);

        var overview = await w.AsAsync("admin", s => s.Get<IDocumentScanning>().OverviewAsync());
        Assert.False(overview.ScannerConnected); Assert.Equal(1, overview.Skipped);
        Assert.Equal(0, (await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync())).Examined);                  // nothing can change with no scanner
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IDocumentScanning>().RescanOneAsync(version)));
        Assert.Equal("scanner_not_connected", refused.Code);

        scanner.Mode = SwitchableScanner.Behaviour.Clean;
        var released = await w.AsAsync("admin", s => s.Get<IDocumentScanJob>().RunAsync());
        Assert.Equal(1, released.Clean);
        Assert.True((await DocumentAsync(w, contract, "Waiting agreement"))!.Downloadable);
    }

    public static async Task Only_an_administrator_of_the_organisation_sees_or_retries_its_files_and_another_tenant_sees_nothing(TestApp app, SwitchableScanner scanner)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var other = await HarborlineWorld.ReadyAsync(app);
        var contract = await w.NewSubcontractAsync();
        scanner.Mode = SwitchableScanner.Behaviour.Errors;
        var version = await AddAsync(w, contract, "Mine", "text");
        scanner.Mode = SwitchableScanner.Behaviour.Clean;

        // People without the permission are refused; another tenant's administrator sees none of it and cannot find the file.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync("packageManagerA", s => s.Get<IDocumentScanning>().OverviewAsync()));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync("auditor", s => s.Get<IDocumentScanning>().HeldAsync()));
        Assert.Equal(0, (await other.AsAsync("admin", s => s.Get<IDocumentScanning>().OverviewAsync())).Held);
        Assert.Empty(await other.AsAsync("admin", s => s.Get<IDocumentScanning>().HeldAsync()));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAsync("admin", s => s.Get<IDocumentScanning>().RescanOneAsync(version)));
        Assert.Single(await w.AsAsync("admin", s => s.Get<IDocumentScanning>().HeldAsync()));
    }
}

[Trait("Category", "Governance")]
public sealed class DocumentScanningSqliteTests
{
    private static Task Run(Func<TestApp, SwitchableScanner, Task> scenario)
    {
        var scanner = new SwitchableScanner();
        return ProviderRunner.SqliteAsync(app => scenario(app, scanner), s => { s.AddSingleton<IMalwareScanner>(scanner); s.Configure<DocumentsOptions>(o => o.MaxScanAttempts = 3); });
    }
    [Fact] public Task Held_and_retried() => Run(DocumentScanningScenarios.A_file_the_scanner_cannot_check_is_held_retried_a_bounded_number_of_times_and_released_only_by_a_clean_result);
    [Fact] public Task Quarantine() => Run(DocumentScanningScenarios.An_infected_file_stays_in_quarantine_is_never_released_or_copied_and_is_audited);
    [Fact] public Task No_scanner() => Run(DocumentScanningScenarios.With_no_scanner_connected_every_file_is_held_and_connecting_one_releases_what_it_finds_clean);
    [Fact] public Task Alerts() => Run(DocumentScanningScenarios.Quarantine_scanner_trouble_and_exhausted_retries_alert_the_administrators_without_naming_the_file);
    [Fact] public Task Isolation() => Run(DocumentScanningScenarios.Only_an_administrator_of_the_organisation_sees_or_retries_its_files_and_another_tenant_sees_nothing);
}

[Trait("Category", "SqlServer")]
public sealed class DocumentScanningSqlServerTests
{
    private static Task Run(Func<TestApp, SwitchableScanner, Task> scenario)
    {
        var scanner = new SwitchableScanner();
        return ProviderRunner.SqlServerAsync(app => scenario(app, scanner), s => { s.AddSingleton<IMalwareScanner>(scanner); s.Configure<DocumentsOptions>(o => o.MaxScanAttempts = 3); });
    }
    [SqlServerFact] public Task Held_and_retried() => Run(DocumentScanningScenarios.A_file_the_scanner_cannot_check_is_held_retried_a_bounded_number_of_times_and_released_only_by_a_clean_result);
    [SqlServerFact] public Task Quarantine() => Run(DocumentScanningScenarios.An_infected_file_stays_in_quarantine_is_never_released_or_copied_and_is_audited);
    [SqlServerFact] public Task No_scanner() => Run(DocumentScanningScenarios.With_no_scanner_connected_every_file_is_held_and_connecting_one_releases_what_it_finds_clean);
    [SqlServerFact] public Task Alerts() => Run(DocumentScanningScenarios.Quarantine_scanner_trouble_and_exhausted_retries_alert_the_administrators_without_naming_the_file);
    [SqlServerFact] public Task Isolation() => Run(DocumentScanningScenarios.Only_an_administrator_of_the_organisation_sees_or_retries_its_files_and_another_tenant_sees_nothing);
}
