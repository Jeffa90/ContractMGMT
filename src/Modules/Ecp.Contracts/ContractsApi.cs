using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Metadata;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Contracts;

/// <summary>
/// The initial workflow state code for new contracts. It comes from the workflow definition (configuration), not from code,
/// so no business-pack state names live in compiled code (PRT-001). Until the workflow engine exists it is set in host configuration.
/// </summary>
public sealed class ContractsOptions
{
    /// <summary>Only for a host with no lifecycle module (the lifecycle definition decides otherwise). Unset by default: no business's state name is a platform default.</summary>
    public string InitialStatusCode { get; set; } = "";
    /// <summary>The most rows one export may contain. A bigger result is refused with a message to narrow it or to ask for a large export.</summary>
    public int ExportRowLimit { get; set; } = 10_000;
    /// <summary>The most rows a large export (explicit request with a reason, audited) may contain.</summary>
    public int LargeExportRowLimit { get; set; } = 100_000;
}

public sealed record CreateCategoryRequest(Guid OrganisationId, string Code, string Name, Guid? ParentCategoryId = null);
public sealed record CreateSupplierRequest(Guid OrganisationId, string Name, string? Abn = null, string? VendorNumber = null, string? SupplierType = null);
public sealed record CreateContractRequest(
    Guid OrganisationId, string AgreementReference, string Title, Guid SupplierId, Guid CategoryId, string? Description = null,
    string Classification = Classifications.Internal, string? CurrencyCode = null, decimal? EstimatedValue = null,
    string? ExtensionType = null, bool LegalReviewRequired = false, DateOnly? StartDate = null, DateOnly? EffectiveDate = null,
    DateOnly? ReviewDate = null, DateOnly? EndDate = null, Guid? BusinessUnitId = null, Guid? SiteId = null,
    Guid? LegalEntityId = null, string? ContractType = null, string? AgreementType = null, string? RiskLevel = null, string? FunctionCode = null,
    string? SapOutlineAgreementNumber = null, int? NoticePeriodValue = null, string? NoticePeriodUnit = null, bool CriticalNotice = false,
    IReadOnlyDictionary<string, string?>? CustomValues = null, CustomFieldInput? CustomInput = null)
{
    /// <summary>The configured field values to store: a form's values (a multiple-choice field posts several) or else the single values given.</summary>
    public CustomFieldInput CustomFieldValues() => CustomInput ?? (CustomValues is null ? new CustomFieldInput() : CustomFieldInput.From(CustomValues));
}

public sealed record ContractDto(
    Guid Id, Guid OrganisationId, string AgreementReference, string Title, string? Description, Guid SupplierId, Guid CategoryId,
    string Status, bool RecordLocked, string Classification, DateTime? RegisteredDateUtc, bool IsLegacyLoad);

public interface ICategoryService { Task<Guid> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default); }
public interface ISupplierService { Task<Guid> CreateAsync(CreateSupplierRequest request, CancellationToken ct = default); }

public interface IContractService
{
    Task<Guid> CreateDraftAsync(CreateContractRequest request, CancellationToken ct = default);
    Task<ContractDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpdateDetailsAsync(Guid id, string title, string? description, CancellationToken ct = default);
}

internal static class AuditJson
{
    public static string Of(object value) => JsonSerializer.Serialize(value);
}

internal sealed class CategoryService(ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, ITenantDirectory tenants) : ICategoryService
{
    public async Task<Guid> CreateAsync(CreateCategoryRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (!await tenants.OrganisationExistsAsync(r.OrganisationId, ct))
            throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        var category = Category.Create(tenantId, r.OrganisationId, r.Code, r.Name, r.ParentCategoryId);
        return await uow.ExecuteAsync(async c =>
        {
            db.Categories.Add(category);
            await Save(db, c);
            audit.Record(new AuditEntry("category.created", nameof(Category), category.Id.ToString(), OrganisationId: category.OrganisationId,
                NewValuesJson: AuditJson.Of(new { category.Code, category.Name })));
            return category.Id;
        }, ct);
    }

    private static async Task Save(ContractsDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        { throw new DomainException("category_code_in_use", "That [[category|lower]] code is already in use."); }
    }
}

internal sealed class SupplierService(ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ITenantDirectory tenants) : ISupplierService
{
    public async Task<Guid> CreateAsync(CreateSupplierRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (!await tenants.OrganisationExistsAsync(r.OrganisationId, ct))
            throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        var supplier = Supplier.Create(tenantId, r.OrganisationId, r.Name, r.Abn, r.VendorNumber, clock.UtcNow, r.SupplierType);
        return await uow.ExecuteAsync(async c =>
        {
            db.Suppliers.Add(supplier);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("vendor_number_in_use", "That vendor number is already in use."); }
            audit.Record(new AuditEntry("supplier.created", nameof(Supplier), supplier.Id.ToString(), OrganisationId: supplier.OrganisationId,
                NewValuesJson: AuditJson.Of(new { supplier.Name })));
            return supplier.Id;
        }, ct);
    }
}

internal sealed class ContractService(
    ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IActorContext actor, IClock clock,
    ITenantDirectory tenants, Microsoft.Extensions.Options.IOptions<ContractsOptions> options, IEnumerable<IContractCreationPolicy> policies, IContractCustomFieldWriter customFields) : IContractService
{
    public async Task<Guid> CreateDraftAsync(CreateContractRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (!await tenants.OrganisationExistsAsync(r.OrganisationId, ct))
            throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        // The workflow definition decides the initial state and pins the contract to the definition in force; without a lifecycle module the configured code is used.
        var policy = policies.FirstOrDefault();
        var setup = policy is null ? null : await policy.ResolveAsync(r.OrganisationId, ct);
        var initial = setup?.InitialStatus ?? options.Value.InitialStatusCode;
        if (string.IsNullOrWhiteSpace(initial))
            throw new InvalidOperationException("The organisation has no initial contract state (it comes from its lifecycle definition; Contracts:InitialStatusCode serves only a host without a lifecycle module).");

        var contract = Contract.CreateDraft(new NewContract(
            tenantId, r.OrganisationId, r.AgreementReference, r.Title, r.SupplierId, r.CategoryId, initial, actor.ActorId ?? "system", clock.UtcNow,
            r.Description, r.Classification, r.CurrencyCode, r.EstimatedValue, r.ExtensionType, r.LegalReviewRequired,
            r.StartDate, r.EffectiveDate, r.ReviewDate, r.EndDate, r.BusinessUnitId, r.SiteId, r.LegalEntityId, r.ContractType, r.AgreementType, r.RiskLevel, r.FunctionCode,
            r.SapOutlineAgreementNumber, r.NoticePeriodValue, r.NoticePeriodUnit, r.CriticalNotice));
        if (setup is not null)
        {
            contract.ApplyWorkflowState(setup.InitialStatus, setup.Effects, actor.ActorId ?? "system", clock.UtcNow);
            if (setup.WorkflowRef is not null) contract.PinWorkflow(setup.WorkflowRef, setup.ConfigReleaseId);
        }

        // Tenant-wide uniqueness (D3). The message never says where a colliding reference lives.
        if (await db.Contracts.AnyAsync(c => c.AgreementReference == contract.AgreementReference, ct))
            throw new DomainException("agreement_reference_in_use", "That [[contract.reference|lower]] is already in use.");
        // Supplier and category must be visible in this tenant (other tenants' rows are invisible through the filter).
        if (!await db.Suppliers.AnyAsync(s => s.Id == r.SupplierId && s.OrganisationId == r.OrganisationId, ct))
            throw new DomainException("supplier_not_found", "The [[supplier|lower]] was not found.");
        if (!await db.Categories.AnyAsync(s => s.Id == r.CategoryId && s.OrganisationId == r.OrganisationId, ct))
            throw new DomainException("category_not_found", "The [[category|lower]] was not found.");

        return await uow.ExecuteAsync(async c =>
        {
            db.Contracts.Add(contract);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("agreement_reference_in_use", "That [[contract.reference|lower]] is already in use."); }
            audit.Record(new AuditEntry("contract.created", nameof(Contract), contract.Id.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                NewValuesJson: AuditJson.Of(new { contract.AgreementReference, contract.ContractTitle, contract.Status, contract.Classification })));
            // Configured fields: values given at creation, and the defaults of the fields that have one. They commit with the contract or not at all.
            await customFields.WriteNewAsync(contract.Id, r.CustomFieldValues(), actor.ActorId ?? "system", c);
            return contract.Id;
        }, ct);
    }

    public Task<ContractDto?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Contracts.AsNoTracking().Where(c => c.Id == id).Select(c => new ContractDto(
            c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Description, c.SupplierId, c.CategoryId, c.Status,
            c.RecordLocked, c.Classification, c.RegisteredDateUtc, c.IsLegacyLoad)).FirstOrDefaultAsync(ct);

    public async Task UpdateDetailsAsync(Guid id, string title, string? description, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var contract = await db.Contracts.FirstOrDefaultAsync(x => x.Id == id, c)
                ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
            var old = AuditJson.Of(new { contract.ContractTitle, contract.Description });
            contract.UpdateDetails(title, description, actor.ActorId ?? "system", clock.UtcNow);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload and try again."); }
            audit.Record(new AuditEntry("contract.updated", nameof(Contract), contract.Id.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                OldValuesJson: old, NewValuesJson: AuditJson.Of(new { contract.ContractTitle, contract.Description })));
        }, ct);
    }
}

internal sealed class ContractDocumentParentValidator(ContractsDbContext db) : Ecp.Documents.IDocumentParentValidator
{
    public Task<bool> ParentExistsAsync(Guid contractId, CancellationToken ct = default)
        => db.Contracts.AsNoTracking().AnyAsync(c => c.Id == contractId, ct);
}

public static class ContractsModule
{
    public const string Name = "Contracts";
    public const string Schema = "contracts";
    public const int Tier = 6;

    public static IServiceCollection AddContractsModule(this IServiceCollection services, Action<ContractsOptions> configure)
    {
        services.Configure(configure);
        services.AddMetadataModule();
        services.AddModuleDbContext<ContractsDbContext>(Name, Tier, Schema);
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<IContractService, ContractService>();
        services.AddScoped<Ecp.Documents.IDocumentParentValidator, ContractDocumentParentValidator>();
        services.AddScoped<ContractAccessContext>();
        services.AddScoped<ReferenceAllocator>();
        services.AddScoped<ICategoryAdministration, CategoryAdministration>();
        services.AddScoped<IAgreementReferenceAllocator, TrustedReferenceAllocator>();
        services.AddScoped<IContractLifecycleService, ContractLifecycleService>();
        services.AddScoped<ISupplierDirectory, SupplierDirectory>();
        services.AddScoped<IAccessSimulator, AccessSimulator>();
        services.AddScoped<IContractMigrationPort, ContractMigrationPort>();
        services.AddScoped<IContractLifecyclePort, ContractLifecyclePort>();
        services.AddScoped<ICutoverFreeze, NoCutoverFreeze>();
        services.AddScoped<ICoreFieldLists, CoreFieldLists>();
        services.AddScoped<IContractChangeHistory, ContractChangeHistory>();
        services.AddScoped<ContractCustomFieldService>();
        services.AddScoped<IContractCustomFields>(sp => sp.GetRequiredService<ContractCustomFieldService>());
        services.AddScoped<IContractCustomFieldWriter>(sp => sp.GetRequiredService<ContractCustomFieldService>());
        services.AddScoped<IContractCustomFieldReader>(sp => sp.GetRequiredService<ContractCustomFieldService>());
        services.AddScoped<Ecp.Metadata.ICustomFieldUsage, ContractCustomFieldUsage>();
        services.AddScoped<Register.RegisterQueryEngine>();
        services.AddScoped<Register.IContractExports, Register.ContractExportService>();
        services.AddScoped<Register.IContractViews, Register.ContractViewService>();
        services.AddScoped<Register.IContractViewStudio, Register.ContractViewStudio>();
        services.AddScoped<Ecp.Configuration.IConfigurationKind, Register.ContractListViewKind>();
        services.AddScoped<Register.IContractRegister>(sp => sp.GetRequiredService<Register.RegisterQueryEngine>());
        services.AddScoped<ISecuredContractService, SecuredContractService>();
        services.AddScoped<ISecuredSupplierService, SecuredSupplierService>();
        services.AddScoped<ISecuredCategoryService, SecuredCategoryService>();
        services.AddScoped<ISecuredContractDocuments, SecuredContractDocuments>();
        services.AddScoped<Ecp.Identity.ICategoryHierarchy, CategoryHierarchyProvider>();
        return services;
    }
}
