using Ecp.Approvals.Definitions;
using Ecp.Approvals.Resolution;
using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;

namespace Ecp.Approvals;

internal sealed class ApprovalSimulator(
    IApprovalResolver resolver, IApprovalMatrixStore store, IAccessService access, ITenantContext tenant, IClock clock, IOrganisationStructureService structure, IUnitOfWork uow, IAuditWriter audit,
    IClassificationScaleProvider scales) : IApprovalSimulator
{
    public async Task<SimulationResult> SimulateAsync(SimulationRequest r, CancellationToken ct = default)
    {
        await access.RequireAsync("approvals.simulate", ApprovalResources.Matrix(tenant.RequireTenantId(), r.OrganisationId), ct);
        var org = await structure.GetOrganisationAsync(r.OrganisationId, ct) ?? throw new ResourceNotFoundException();
        var zone = TimeZones.Find(org.TimeZoneId);
        var today = TimeZones.LocalDate(clock.UtcNow, zone);
        // A chosen date means "midday that day in the organisation's time zone", so the date and the people in force at that moment are both unambiguous. No date means right now.
        var asOfUtc = r.AsOfDate is { } d ? TimeZones.ToUtc(d.ToDateTime(new TimeOnly(12, 0)), zone) : clock.UtcNow;
        var date = TimeZones.LocalDate(asOfUtc, zone);

        var notes = new List<string>();
        if (r.Source.Kind != MatrixSourceKind.Published)
        {
            var loaded = await store.LoadAsync(r.OrganisationId, r.Source, ct);
            var errors = MatrixParser.Validate(loaded.Set, await scales.GetAsync(ct)).Where(m => m.Severity == Ecp.Configuration.ValidationSeverity.Error).ToList();
            if (errors.Count > 0) notes.Add($"This version has {errors.Count} validation error(s) and could not be published as it stands: {string.Join(" | ", errors.Take(3).Select(e => e.Message))}");
        }
        if (date > today) notes.Add($"{date:yyyy-MM-dd} is in the future: rows, rates and assignments that start by then are in force; anything that ends before then is not.");
        if (date < today) notes.Add($"{date:yyyy-MM-dd} is in the past: the matrix text and group assignments are judged as they stand today, restricted to what was in force on that date.");

        var resolution = await resolver.ResolveAsync(new ResolutionRequest(r.OrganisationId, r.MatrixCode, r.Facts, r.Resource, r.SubmitterUserId, asOfUtc, r.Source), ct);

        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("approval.simulated", "ApprovalMatrix", r.MatrixCode, OrganisationId: r.OrganisationId, Classification: r.Resource.Classification,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    matrix = r.MatrixCode, source = resolution.SourceLabel, release = resolution.ReleaseNumber, asOf = date.ToString("yyyy-MM-dd"), outcome = resolution.Resolved ? "resolved" : resolution.BlockCode,
                    rows = resolution.RowCodes, steps = resolution.Plan?.Steps.Count
                })));
            return Task.CompletedTask;
        }, ct);
        return new SimulationResult(resolution, date > today, date < today, notes);
    }

    public async Task<IReadOnlyList<MatrixSummary>> MatricesAsync(Guid organisationId, MatrixSource source, CancellationToken ct = default)
    {
        await access.RequireAsync("approvals.matrix.read", ApprovalResources.Matrix(tenant.RequireTenantId(), organisationId), ct);
        var org = await structure.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
        var today = TimeZones.LocalDate(clock.UtcNow, TimeZones.Find(org.TimeZoneId));
        var loaded = source.Kind == MatrixSourceKind.Published ? await store.TryLoadPublishedAsync(organisationId, ct) : await store.LoadAsync(organisationId, source, ct);
        if (loaded is null) return [];
        return loaded.Set.Matrices.Select(m => new MatrixSummary(m.Code, m.Name, m.Currency, m.Rows.Count(x => x.Kind == RowKinds.Base), m.Rows.Count(x => x.Kind == RowKinds.Exception), m.EffectiveFrom, m.EffectiveTo, m.IsEffectiveOn(today))).ToList();
    }
}
