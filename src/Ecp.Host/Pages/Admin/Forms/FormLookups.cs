using Ecp.Approvals;
using Ecp.Configuration.Forms;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Metadata;
using Ecp.SharedKernel;

namespace Ecp.Host.Pages.Admin.Forms;

/// <summary>
/// The lists a form offers where a choice depends on the installation: permissions, classification levels, approver groups, published matrices, the structure and category codes, and the fields a workflow may require.
/// Each list is read with the signed-in person's own rights; one the person may not read is simply empty, and the field then accepts the value as typed.
/// </summary>
public sealed class FormLookups(IClassificationScaleProvider scales, IApproverGroupService groups, IApprovalMatrixStore matrices, IApprovalReferenceData? reference, ICustomFieldCatalogue fields) : IFormLookupProvider
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FormOption>>> LookupsAsync(Guid organisationId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<FormOption>>(StringComparer.Ordinal);
        static IReadOnlyList<FormOption> Same(IEnumerable<string> values) => values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Select(v => new FormOption(v, v)).ToList();
        var matrix = PermissionMatrix.Instance;
        result["permissions"] = matrix.Permissions.OrderBy(p => p, StringComparer.Ordinal).Select(p => new FormOption(p, matrix.Info(p).Description is { Length: > 0 } d ? $"{p}: {d}" : p)).ToList();
        try { result["levels"] = (await scales.GetAsync(ct)).Levels.Select(l => new FormOption(l, l)).ToList(); } catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        try { result["groups"] = Same((await groups.ListAsync(ct)).Where(g => g.IsActive).Select(g => g.Key)); } catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        try { result["matrices"] = Same((await matrices.TryLoadPublishedAsync(organisationId, ct))?.Set.Matrices.Select(m => m.Code) ?? []); } catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        try
        {
            if (reference is not null)
            {
                var data = await reference.LoadAsync(organisationId, ct);
                result["categories"] = Same(data.CategoryAncestry.Keys);
                result["businessUnits"] = Same(data.BusinessUnits);
                result["sites"] = Same(data.Sites);
                result["legalEntities"] = Same(data.LegalEntities);
                result["contractTypes"] = Same(data.ContractTypes);
            }
        }
        catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        try
        {
            var set = await fields.PublishedAsync(organisationId, ct);
            result["requiredFields"] = Ecp.Lifecycle.Definitions.WorkflowVocabulary.RequiredFieldNames.Concat(set.Fields.Where(f => !f.IsRetired).Select(f => "cf." + f.Key)).Distinct(StringComparer.Ordinal)
                .Select(f => new FormOption(f, f)).ToList();
        }
        catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { }
        result.TryAdd("requiredFields", Ecp.Lifecycle.Definitions.WorkflowVocabulary.RequiredFieldNames.Select(f => new FormOption(f, f)).ToList());
        return result;
    }
}
