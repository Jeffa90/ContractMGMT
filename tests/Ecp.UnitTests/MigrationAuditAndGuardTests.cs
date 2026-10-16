using Ecp.Audit;
using Ecp.Audit.Domain;
using Ecp.DataMigration.Domain;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

public class MigrationBatchTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    private static MigrationProject Project() => MigrationProject.Create(Guid.NewGuid(), Guid.NewGuid(), "Medius load", "Medius", "2026.1", "data.owner", Now.AddDays(-1), Now);

    private static MigrationBatch Validated(BatchKind kind = BatchKind.Final)
    {
        var b = Project().CreateBatch(kind, DuplicateStrategy.SkipExisting, DocumentDuplicateOption.AddVersion, false, null, null, Now);
        b.MarkProfiled(); b.MarkValidated();
        return b;
    }

    private static readonly ReconciliationTotals Balanced = new(10, 8, 1, 1, 6, 2);

    [Fact]
    public void Totals_balance_only_when_source_equals_accepted_rejected_skipped_and_accepted_equals_created_updated()
    {
        Assert.True(Balanced.IsBalanced);
        Assert.False(new ReconciliationTotals(10, 8, 1, 0, 6, 2).IsBalanced);
        Assert.False(new ReconciliationTotals(10, 8, 1, 1, 5, 2).IsBalanced);
        Assert.False(new ReconciliationTotals(1, 2, -1, 0, 2, 0).IsBalanced);
    }

    [Fact]
    public void A_dry_run_can_never_commit()
    {
        var b = Validated(BatchKind.DryRun);
        Assert.Equal("dry_run_cannot_commit", Assert.Throws<DomainException>(() => b.StartCommit()).Code);
        Assert.Equal(BatchStatus.Validated, b.Status);
    }

    [Fact]
    public void Commit_requires_a_validated_batch_and_states_cannot_be_skipped()
    {
        var b = Project().CreateBatch(BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.RejectDuplicateHash, false, null, null, Now);
        Assert.Equal("invalid_batch_transition", Assert.Throws<DomainException>(() => b.StartCommit()).Code);
        Assert.Throws<DomainException>(() => b.MarkValidated());
        b.MarkProfiled();
        Assert.Throws<DomainException>(() => b.CompleteCommit());
    }

    [Fact]
    public void A_failed_commit_can_be_resumed()
    {
        var b = Validated();
        b.StartCommit();
        b.Fail("process stopped");
        Assert.Equal(BatchStatus.Failed, b.Status);
        b.StartCommit();
        Assert.Equal(BatchStatus.Committing, b.Status);
        Assert.Null(b.FailureReason);
    }

    [Fact]
    public void A_batch_cannot_be_reconciled_unless_totals_balance_or_exceptions_are_approved()
    {
        var b = Validated(); b.StartCommit(); b.CompleteCommit();
        var unbalanced = new ReconciliationTotals(10, 8, 1, 0, 6, 2);
        Assert.Equal("not_balanced", Assert.Throws<DomainException>(() => b.Reconcile(unbalanced, exceptionsApproved: false)).Code);
        Assert.Equal(BatchStatus.Committed, b.Status);
        b.Reconcile(unbalanced, exceptionsApproved: true);
        Assert.Equal(BatchStatus.Reconciled, b.Status);
        Assert.True(b.ExceptionsApproved);
    }

    [Fact]
    public void Accepted_batches_cannot_be_rolled_back_or_compensated()
    {
        var b = Validated(); b.StartCommit(); b.CompleteCommit(); b.Reconcile(Balanced, false);
        b.Accept("data.owner", Now);
        Assert.Equal("rollback_after_acceptance", Assert.Throws<DomainException>(() => b.RollBack()).Code);
        Assert.Equal("rollback_after_acceptance", Assert.Throws<DomainException>(() => b.Compensate()).Code);
        Assert.Equal(BatchStatus.Accepted, b.Status);
    }

    [Fact]
    public void Rollback_is_allowed_before_acceptance()
    {
        var b = Validated(); b.StartCommit(); b.CompleteCommit();
        b.RollBack();
        Assert.Equal(BatchStatus.RolledBack, b.Status);
    }

    [Fact]
    public void Legacy_load_needs_an_active_unexpired_policy_and_is_not_available_to_dry_runs()
    {
        var p = Project();
        Assert.Equal("legacy_policy_inactive", Assert.Throws<DomainException>(() =>
            p.CreateBatch(BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, true, null, null, Now)).Code);
        p.EnableLegacyLoad(allowRegisteredDateImport: true, Now.AddDays(30), Now);
        var b = p.CreateBatch(BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, true, null, null, Now);
        Assert.True(b.IsLegacyLoad);
        Assert.True(b.AllowRegisteredDateImport);
        Assert.Throws<DomainException>(() => p.CreateBatch(BatchKind.DryRun, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, true, null, null, Now));
        Assert.Throws<DomainException>(() => p.CreateBatch(BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, true, null, null, Now.AddDays(31)));
        p.CloseLegacyLoad();
        Assert.Throws<DomainException>(() => p.CreateBatch(BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, true, null, null, Now));
    }

    [Fact]
    public void A_freeze_window_must_end_after_it_starts_and_a_batch_inherits_tenant_and_organisation()
    {
        var p = Project();
        Assert.Throws<DomainException>(() => p.SetFreezeWindow(Now, Now));
        p.SetFreezeWindow(Now, Now.AddHours(8));
        var b = p.CreateBatch(BatchKind.Mock, DuplicateStrategy.UpdateMapped, DocumentDuplicateOption.LinkExisting, false, null, null, Now);
        Assert.Equal(p.TenantId, b.TenantId);
        Assert.Equal(p.OrganisationId, b.OrganisationId);
    }
}

public class AuditChainTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly ActorContext Actor = new() { ActorId = "tester", Source = "Web", CorrelationId = "corr-1" };

    private static List<AuditEvent> Chain(int n)
    {
        var list = new List<AuditEvent>(); var prev = AuditEvent.GenesisHash;
        for (var i = 1; i <= n; i++)
        {
            var e = AuditEvent.Create(Tenant, i, T0.AddSeconds(i), Actor, new AuditEntry("contract.created", "Contract", $"id-{i}", NewValuesJson: "{\"a\":1}"), prev);
            list.Add(e); prev = e.Hash;
        }
        return list;
    }

    [Fact]
    public void A_well_formed_chain_verifies()
    {
        var result = AuditChain.Verify(Chain(25));
        Assert.True(result.IsValid);
        Assert.Equal(25, result.EventsChecked);
    }

    [Fact]
    public void An_empty_chain_is_valid()
        => Assert.True(AuditChain.Verify([]).IsValid);

    [Fact]
    public void Changed_content_is_detected_at_the_exact_event()
    {
        var chain = Chain(10);
        typeof(AuditEvent).GetProperty(nameof(AuditEvent.Action))!.SetValue(chain[4], "contract.deleted"); // simulated tampering
        var result = AuditChain.Verify(chain);
        Assert.False(result.IsValid);
        Assert.Equal(5, result.FirstBrokenSequence);
        Assert.Equal(4, result.EventsChecked);
    }

    [Fact]
    public void A_removed_event_is_detected_as_a_sequence_gap()
    {
        var chain = Chain(6); chain.RemoveAt(2);
        var result = AuditChain.Verify(chain);
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstBrokenSequence);
    }

    [Fact]
    public void A_reordered_or_relinked_event_is_detected()
    {
        var chain = Chain(5);
        typeof(AuditEvent).GetProperty(nameof(AuditEvent.PreviousHash))!.SetValue(chain[3], AuditEvent.GenesisHash);
        Assert.False(AuditChain.Verify(chain).IsValid);
    }

    [Fact]
    public void The_hash_covers_every_field_and_is_stable()
    {
        var a = Chain(1)[0];
        Assert.Equal(AuditHasher.Compute(a, a.PreviousHash), a.Hash);
        Assert.Equal(64, a.Hash.Length);
        var b = AuditEvent.Create(Tenant, 1, T0.AddSeconds(1), Actor, new AuditEntry("contract.created", "Contract", "id-1", NewValuesJson: "{\"a\":2}"), AuditEvent.GenesisHash);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Canonical_form_is_unambiguous_for_adjacent_values()
    {
        var x = AuditEvent.Create(Tenant, 1, T0, Actor, new AuditEntry("ab", "c", "d"), AuditEvent.GenesisHash);
        var y = AuditEvent.Create(Tenant, 1, T0, Actor, new AuditEntry("a", "bc", "d"), AuditEvent.GenesisHash);
        Assert.NotEqual(x.Hash, y.Hash);
    }
}

public class ProductionGuardTests
{
    private static StartupFacts Facts(EcpMode mode = EcpMode.Production, DatabaseProvider provider = DatabaseProvider.SqlServer, bool secret = true, bool inline = false,
        bool demoTenant = false, bool seed = false, string? dir = null)
        => new(mode, provider, secret, inline, demoTenant, seed, dir ?? "/var/lib/ecp");

    [Fact]
    public void A_compliant_production_configuration_has_no_violations()
        => Assert.Empty(ProductionGuard.Evaluate(Facts()));

    [Theory]
    [InlineData(EcpMode.Demo)]
    [InlineData(EcpMode.Test)]
    public void Demo_and_test_modes_are_not_restricted(EcpMode mode)
        => Assert.Empty(ProductionGuard.Evaluate(Facts(mode, DatabaseProvider.Sqlite, secret: false, inline: true, demoTenant: true, seed: true, dir: "/tmp/x")));

    [Fact]
    public void Production_refuses_demonstration_tenant_resolution()
        => Assert.Contains(ProductionGuard.Evaluate(Facts(demoTenant: true)), v => v.CheckId == "R-ID-01");

    [Fact]
    public void Production_refuses_the_lightweight_provider()
        => Assert.Contains(ProductionGuard.Evaluate(Facts(provider: DatabaseProvider.Sqlite)), v => v.CheckId == "R-DB-01");

    [Fact]
    public void Production_refuses_inline_connection_strings_and_requires_a_secret_reference()
    {
        Assert.Contains(ProductionGuard.Evaluate(Facts(secret: false, inline: true)), v => v.CheckId == "R-SEC-02");
        Assert.Contains(ProductionGuard.Evaluate(Facts(secret: false, inline: false)), v => v.CheckId == "R-SEC-02");
    }

    [Fact]
    public void Production_refuses_demonstration_data_and_ephemeral_storage()
    {
        Assert.Contains(ProductionGuard.Evaluate(Facts(seed: true)), v => v.CheckId == "R-DEMO-01");
        Assert.Contains(ProductionGuard.Evaluate(Facts(dir: Path.GetTempPath())), v => v.CheckId == "R-ST-01");
        Assert.Contains(ProductionGuard.Evaluate(Facts(dir: "")), v => v.CheckId == "R-ST-01");
    }

    [Fact]
    public void Several_problems_are_all_reported_together()
        => Assert.True(ProductionGuard.Evaluate(Facts(provider: DatabaseProvider.Sqlite, secret: false, inline: true, demoTenant: true, seed: true, dir: "/tmp/z")).Count >= 5);
}
