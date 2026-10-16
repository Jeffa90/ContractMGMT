using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>Helpers for custom field and register scenarios: publishing field definitions, reading and saving the configured fields of a contract as a given person.</summary>
internal static class CustomFieldWorld
{
    public static string Field(string key, string type, string extra = "") => $"{{ \"key\": \"{key}\", \"label\": \"{key} label\", \"type\": \"{type}\"{(extra.Length == 0 ? "" : ", " + extra)} }}";
    public static string Doc(string fields, string extra = "") => $"{{ \"schemaVersion\": 1{(extra.Length == 0 ? "" : ", " + extra)}, \"fields\": [ {fields} ] }}";
    public const string Choices = "\"choices\": [ { \"code\": \"GOLD\", \"label\": \"Gold\" }, { \"code\": \"SILVER\", \"label\": \"Silver\" }, { \"code\": \"BRONZE\", \"label\": \"Bronze\" } ]";

    public static Task<ReleaseDto> PublishFieldsAsync(this LifecycleWorld w, string json, Guid? org = null)
        => w.W.AsScopeless().Run(s => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(MetadataModule.CustomFieldKind, org ?? w.W.Org1, json, "test", "fields for a scenario"));

    public static Task<CustomFieldForm> FormAsync(this LifecycleWorld w, string user, Guid contractId, CustomFieldInput? submitted = null)
        => w.As(user, s => s.Get<IContractCustomFields>().ForContractAsync(contractId, submitted));

    public static Task<IReadOnlyList<FieldChange>> SaveFieldsAsync(this LifecycleWorld w, string user, Guid contractId, params (string Key, string?[] Values)[] values)
        => w.As(user, s => s.Get<IContractCustomFields>().SaveAsync(contractId, new CustomFieldInput(values.Select(v => new KeyValuePair<string, IReadOnlyList<string?>>(v.Key, v.Values)))));

    public static CustomFieldEntry? Entry(this CustomFieldForm form, string key) => form.Fields.FirstOrDefault(f => f.Definition.Key == key);

    public static async Task<Guid> CreateWithFieldsAsync(this LifecycleWorld w, string user, Dictionary<string, string?> values, string title = "Fields", string classification = "Internal", decimal value = 1000m, string? contractType = null)
    {
        var id = await w.As(user, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.W.Org1, $"CF-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}", title, w.W.Sup1, w.W.CatPackaging,
            Classification: classification, CurrencyCode: "AUD", EstimatedValue: value, ContractType: contractType, CustomValues: values)));
        return id;
    }

    public static Task<int> StoredValueCountAsync(this LifecycleWorld w, Guid contractId)
        => w.W.AsScopeless().Run(s => s.Get<Ecp.Contracts.Persistence.ContractsDbContext>().CustomValues.AsNoTracking().CountAsync(v => v.ContractId == contractId));
}
