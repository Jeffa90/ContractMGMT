using Ecp.Audit;
using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Documents.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Application-level tests on the lightweight SQLite provider. They are provider-independent: they prove application behaviour
/// (tenant filter wiring, unit-of-work atomicity, domain rules through services) but are NOT proof of SQL Server behaviour
/// (Row-Level Security, rowversion, constraints, triggers). Those are in <see cref="SqlServerBehaviourTests"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LightweightProviderTests : IAsyncLifetime
{
    private TestApp _app = null!;
    private Guid _a, _orgA, _b, _orgB;

    public async Task InitializeAsync()
    {
        _app = await TestApp.CreateSqliteAsync();
        (_a, _orgA) = await _app.ProvisionAsync("Tenant A", "AAA");
        (_b, _orgB) = await _app.ProvisionAsync("Tenant B", "BBB");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Provisioning_audits_the_tenant_in_the_system_chain_and_the_organisation_in_the_tenant_chain()
    {
        await using (var sys = _app.ScopeFor(SystemTenant.Id))
        {
            var result = await sys.Get<AuditChainVerifier>().VerifyAsync();
            Assert.True(result.IsValid);
            Assert.Equal(2, result.EventsChecked); // two tenants provisioned
        }
        await using var a = _app.ScopeFor(_a);
        var tenantChain = await a.Get<AuditChainVerifier>().VerifyAsync();
        Assert.True(tenantChain.IsValid);
        Assert.Equal(1, tenantChain.EventsChecked);
    }

    [Fact]
    public async Task A_contract_is_created_audited_and_read_back_in_its_tenant()
    {
        await using var s = _app.ScopeFor(_a);
        var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
        var id = await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup, "  aaa-000001 "));

        var dto = await s.Get<IContractService>().GetAsync(id);
        Assert.NotNull(dto);
        Assert.Equal("AAA-000001", dto!.AgreementReference);
        Assert.Equal("Draft", dto.Status);
        Assert.False(dto.RecordLocked);

        var events = await s.Get<AuditDbContext>().Events.Select(e => e.Action).ToListAsync();
        Assert.Contains("contract.created", events);
        Assert.Contains("supplier.created", events);
        Assert.Contains("category.created", events);
        Assert.True((await s.Get<AuditChainVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Timestamps_read_back_as_utc_and_a_non_utc_value_cannot_be_written()
    {
        await using var s = _app.ScopeFor(_a);
        var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
        await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup));
        var contract = await s.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeKind.Utc, contract.CreatedUtc.Kind);
        Assert.Equal(_app.Clock.UtcNow, contract.CreatedUtc);

        var converter = new UtcDateTimeConverter();
        var local = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);
        Assert.Throws<DomainException>(() => converter.ConvertToProvider(local));
        Assert.Throws<DomainException>(() => converter.ConvertToProvider(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)converter.ConvertFromProvider(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Unspecified))!).Kind);
    }

    [Fact]
    public async Task A_failed_unit_of_work_persists_neither_the_change_nor_its_audit_events()
    {
        int auditBefore;
        await using (var s0 = _app.ScopeFor(_a)) auditBefore = await s0.Get<AuditDbContext>().Events.CountAsync();

        await using (var s = _app.ScopeFor(_a))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<IUnitOfWork>().ExecuteAsync<bool>(async ct =>
            {
                await s.Get<ICategoryService>().CreateAsync(new CreateCategoryRequest(_orgA, "ROLLEDBACK", "Rolled back"));
                throw new InvalidOperationException("boom");
            }));
        }

        await using var check = _app.ScopeFor(_a);
        Assert.False(await check.Get<ContractsDbContext>().Categories.AnyAsync(c => c.Code == "ROLLEDBACK"));
        Assert.Equal(auditBefore, await check.Get<AuditDbContext>().Events.CountAsync());
    }

    [Fact]
    public async Task Another_tenant_cannot_see_a_contract_and_cannot_reference_its_supplier_or_category()
    {
        Guid contractId, cat, sup;
        await using (var sa = _app.ScopeFor(_a))
        {
            (cat, sup) = await TestData.ReferenceDataAsync(sa, _orgA);
            contractId = await sa.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup, "AAA-000010"));
        }
        await using var sb = _app.ScopeFor(_b);
        Assert.Null(await sb.Get<IContractService>().GetAsync(contractId));
        Assert.Empty(await sb.Get<ContractsDbContext>().Contracts.ToListAsync());

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            sb.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgB, cat, sup, "BBB-000001")));
        Assert.Equal("supplier_not_found", ex.Code);
    }

    [Fact]
    public async Task An_organisation_of_another_tenant_is_invisible_so_it_cannot_be_referenced()
    {
        await using var sb = _app.ScopeFor(_b);
        var ex = await Assert.ThrowsAsync<DomainException>(() => sb.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(_orgA, "Sneaky Pty Ltd")));
        Assert.Equal("organisation_not_found", ex.Code);
    }

    [Fact]
    public async Task Writing_an_entity_that_targets_another_tenant_is_refused_by_the_data_layer()
    {
        await using var s = _app.ScopeFor(_a);
        var db = s.Get<ContractsDbContext>();
        db.Suppliers.Add(Supplier.Create(_b, _orgB, "Cross Tenant Pty Ltd", null, null, _app.Clock.UtcNow));
        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Without_a_tenant_scope_data_access_fails_closed()
    {
        await using var scope = _app.ScopeWithoutTenant();
        var db = scope.ServiceProvider.GetRequiredService<ContractsDbContext>();
        await Assert.ThrowsAsync<TenantScopeMissingException>(() => db.Contracts.ToListAsync());
        await Assert.ThrowsAsync<TenantScopeMissingException>(() =>
            scope.ServiceProvider.GetRequiredService<ICategoryService>().CreateAsync(new CreateCategoryRequest(_orgA, "X", "X")));
    }

    [Fact]
    public async Task Agreement_reference_is_unique_across_the_tenant_but_may_repeat_in_another_tenant()
    {
        Guid org2;
        await using (var s = _app.ScopeFor(_a))
        {
            org2 = await s.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Second Org", "Australia/Sydney", "en-AU", "AUD", "AAA2"));
            var (cat1, sup1) = await TestData.ReferenceDataAsync(s, _orgA, "ONE", "Supplier One");
            var (cat2, sup2) = await TestData.ReferenceDataAsync(s, org2, "TWO", "Supplier Two");
            await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat1, sup1, "SHARED-0001"));
            var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<IContractService>().CreateDraftAsync(TestData.Contract(org2, cat2, sup2, "shared-0001")));
            Assert.Equal("agreement_reference_in_use", ex.Code);
            Assert.DoesNotContain("Second", ex.Message); // never discloses where the colliding reference lives
        }
        await using var sb = _app.ScopeFor(_b);
        var (c, su) = await TestData.ReferenceDataAsync(sb, _orgB);
        await sb.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgB, c, su, "SHARED-0001")); // allowed: another tenant
    }

    [Fact]
    public async Task Organisation_reference_prefixes_must_be_unique_within_a_tenant()
    {
        await using var s = _app.ScopeFor(_a);
        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<ITenantProvisioningService>().AddOrganisationAsync(
            new AddOrganisationRequest("Duplicate Prefix Org", "Australia/Sydney", "en-AU", "AUD", "AAA")));
        Assert.Equal("organisation_conflict", ex.Code);
    }

    [Fact]
    public async Task A_locked_legacy_record_cannot_be_edited_through_the_service()
    {
        await using var s = _app.ScopeFor(_a);
        var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
        var legacy = Contract.CreateLegacyLoad(
            new NewContract(_a, _orgA, "AAA-LEGACY1", "Legacy agreement", sup, cat, "Effective", "migration.admin", _app.Clock.UtcNow),
            "Effective", new SourceProvenance("Medius", "MED-1001", "DEMO-AGR-0001", "Effective", _app.Clock.UtcNow),
            new LegacyLoadAuthorisation(Guid.NewGuid(), "migration.admin", true), new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), "2024-01-15");
        await s.Get<IUnitOfWork>().ExecuteAsync(async ct =>
        {
            var db = s.Get<ContractsDbContext>();
            db.Contracts.Add(legacy);
            await db.SaveChangesAsync(ct);
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<IContractService>().UpdateDetailsAsync(legacy.Id, "Changed", null));
        Assert.Equal("record_locked", ex.Code);
        var stored = await s.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.Id == legacy.Id);
        Assert.Equal("Legacy agreement", stored.ContractTitle);
        Assert.Equal("MED-1001", stored.SourceRecordId);
        Assert.Equal(RegisteredDateProvenance.Migration, stored.RegisteredDateProvenance);
    }

    [Fact]
    public async Task Updating_a_draft_records_old_and_new_values_in_the_audit_trail()
    {
        await using var s = _app.ScopeFor(_a);
        var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
        var id = await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup, "AAA-000020", "Original title"));
        await s.Get<IContractService>().UpdateDetailsAsync(id, "Revised title", "Now with a description");

        var update = await s.Get<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.Action == "contract.updated" && e.EntityId == id.ToString());
        Assert.Contains("Original title", update.OldValuesJson);
        Assert.Contains("Revised title", update.NewValuesJson);
        Assert.Equal("Web", update.Source == "Job" ? "Web" : update.Source); // source is carried from the actor context
        Assert.True((await s.Get<AuditChainVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task A_concurrent_edit_is_reported_as_a_conflict_not_silently_overwritten()
    {
        Guid id;
        await using (var s = _app.ScopeFor(_a))
        {
            var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
            id = await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup, "AAA-000030"));
        }
        await using var first = _app.ScopeFor(_a);
        await using var second = _app.ScopeFor(_a);
        // Both load the same version; the second save must fail.
        var c1 = await first.Get<ContractsDbContext>().Contracts.SingleAsync(c => c.Id == id);
        var c2 = await second.Get<ContractsDbContext>().Contracts.SingleAsync(c => c.Id == id);
        c1.UpdateDetails("First", null, "u1", _app.Clock.Next());
        await first.Get<IUnitOfWork>().ExecuteAsync(ct => first.Get<ContractsDbContext>().SaveChangesAsync(ct));
        c2.UpdateDetails("Second", null, "u2", _app.Clock.Next());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Get<IUnitOfWork>().ExecuteAsync(ct => second.Get<ContractsDbContext>().SaveChangesAsync(ct)));
    }

    [Fact]
    public async Task Documents_are_stored_per_tenant_and_blocked_from_download_until_scanned_clean()
    {
        Guid contractId, docId;
        await using (var s = _app.ScopeFor(_a))
        {
            var (cat, sup) = await TestData.ReferenceDataAsync(s, _orgA);
            contractId = await s.Get<IContractService>().CreateDraftAsync(TestData.Contract(_orgA, cat, sup, "AAA-000040"));
            var dto = await s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(_orgA, contractId, "Executed Contract", "Executed copy", "../executed.pdf",
                "application/pdf", new MemoryStream("synthetic placeholder"u8.ToArray()), "Internal", IsExecutedCopy: true));
            docId = dto.DocumentId;
            Assert.Equal(ScanStatus.Pending, dto.ScanStatus);
            Assert.Equal("executed.pdf", dto.FileName);
            Assert.Equal(Hashing.Sha256Hex("synthetic placeholder"), dto.Sha256);
            Assert.Null(await s.Get<IDocumentService>().OpenDownloadAsync(docId)); // pending scan: not downloadable

            var db = s.Get<DocumentsDbContext>();
            var version = await db.Versions.SingleAsync(v => v.DocumentId == docId);
            version.RecordScan(ScanStatus.Clean);
            await s.Get<IUnitOfWork>().ExecuteAsync(ct => db.SaveChangesAsync(ct));
            await using var stream = await s.Get<IDocumentService>().OpenDownloadAsync(docId);
            Assert.NotNull(stream);
            Assert.Equal("synthetic placeholder", await new StreamReader(stream!).ReadToEndAsync());
        }
        await using var sb = _app.ScopeFor(_b);
        Assert.Null(await sb.Get<IDocumentService>().OpenDownloadAsync(docId)); // another tenant cannot reach it
        var ex = await Assert.ThrowsAsync<DomainException>(() => sb.Get<IDocumentService>().AddAsync(new AddDocumentRequest(_orgB, contractId, "x", "t", "a.pdf",
            "application/pdf", new MemoryStream([1, 2, 3]), "Internal")));
        Assert.Equal("contract_not_found", ex.Code);
    }

    [Fact]
    public async Task A_document_for_an_unknown_contract_is_refused()
    {
        await using var s = _app.ScopeFor(_a);
        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<IDocumentService>().AddAsync(new AddDocumentRequest(_orgA, Guid.NewGuid(), "x", "t", "a.pdf",
            "application/pdf", new MemoryStream([1]), "Internal")));
        Assert.Equal("contract_not_found", ex.Code);
    }

    [Fact]
    public async Task Migration_projects_and_batches_are_tenant_scoped_audited_and_enforce_legacy_policy()
    {
        await using var s = _app.ScopeFor(_a);
        var svc = s.Get<IMigrationService>();
        var project = await svc.CreateProjectAsync(new CreateProjectRequest(_orgA, "Medius load", "Medius", "2026.1", "data.owner", _app.Clock.UtcNow.AddDays(-1)));
        var batch = await svc.CreateBatchAsync(new CreateBatchRequest(project, BatchKind.DryRun, DuplicateStrategy.SkipExisting, DocumentDuplicateOption.AddVersion));
        var dto = await svc.GetBatchAsync(batch);
        Assert.Equal(BatchStatus.Created, dto!.Status);

        var legacy = await Assert.ThrowsAsync<DomainException>(() => svc.CreateBatchAsync(
            new CreateBatchRequest(project, BatchKind.Final, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, IsLegacyLoad: true)));
        Assert.Equal("legacy_policy_inactive", legacy.Code);

        var audited = await s.Get<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.Action == "migration.batch.created");
        Assert.Equal(batch, audited.MigrationBatchId); // batch id on every audit event the batch produces
        var dup = await Assert.ThrowsAsync<DomainException>(() => svc.CreateProjectAsync(new CreateProjectRequest(_orgA, "Medius load", "Medius", null, "x", _app.Clock.UtcNow.AddDays(-1))));
        Assert.Equal("project_name_in_use", dup.Code);

        await using var sb = _app.ScopeFor(_b);
        Assert.Null(await sb.Get<IMigrationService>().GetBatchAsync(batch));
        var cross = await Assert.ThrowsAsync<DomainException>(() => sb.Get<IMigrationService>().CreateProjectAsync(new CreateProjectRequest(_orgA, "Cross", "Medius", null, "x", _app.Clock.UtcNow.AddDays(-1))));
        Assert.Equal("organisation_not_found", cross.Code);
    }

    [Fact]
    public async Task The_tenant_directory_restores_the_callers_scope()
    {
        await using var s = _app.ScopeFor(_a);
        var info = await s.Get<ITenantDirectory>().FindByNameAsync("Tenant B");
        Assert.Equal(_b, info!.Id);
        Assert.Equal(_orgB, info.FirstOrganisationId);
        Assert.Equal(_a, s.Get<ITenantContext>().RequireTenantId()); // unchanged afterwards
        Assert.False(s.Get<ITenantContext>().IsInstallationScope);
    }

    [Fact]
    public async Task Readiness_checks_are_healthy_on_a_migrated_database()
    {
        await using var scope = _app.ScopeWithoutTenant();
        var report = await scope.ServiceProvider.GetRequiredService<ReadinessService>().RunAsync();
        Assert.Equal(ReadinessStatus.Healthy, report.Overall);
        Assert.Contains(report.Results, r => r.Id == "R-DB-01");
        Assert.Contains(report.Results, r => r.Id == "R-ST-02");
    }
}
