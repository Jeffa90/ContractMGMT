using Ecp.Contracts;
using Ecp.Tests.Common;

namespace Ecp.Integration.Tests;

internal static class TestData
{
    /// <summary>Creates a category, a supplier and returns their ids (inside an established tenant scope).</summary>
    public static async Task<(Guid CategoryId, Guid SupplierId)> ReferenceDataAsync(TenantScope s, Guid organisationId, string categoryCode = "PACKAGING", string supplier = "Southern Cross Demo Supplies Pty Ltd")
    {
        var cat = await s.Get<ICategoryService>().CreateAsync(new CreateCategoryRequest(organisationId, categoryCode, categoryCode));
        var sup = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(organisationId, supplier));
        return (cat, sup);
    }

    public static CreateContractRequest Contract(Guid organisationId, Guid category, Guid supplier, string reference = "DEMO-000001", string title = "Synthetic Packaging Supply Agreement")
        => new(organisationId, reference, title, supplier, category, CurrencyCode: "AUD", EstimatedValue: 125000m);
}
