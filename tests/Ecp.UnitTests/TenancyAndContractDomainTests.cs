using Ecp.Contracts.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy.Domain;
using Xunit;

namespace Ecp.UnitTests;

public class TenancyDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("JBSAUS", "-", 6, 42, "JBSAUS-000042")]
    [InlineData("ac", "/", 4, 7, "AC/0007")]
    [InlineData("X1", "", 3, 123, "X1123")]
    public void Reference_numbering_formats_with_prefix_separator_and_padding(string prefix, string sep, int digits, long n, string expected)
        => Assert.Equal(expected, ReferenceNumberingRule.Create(prefix, sep, digits).Format(n));

    [Theory]
    [InlineData("1ABC")]
    [InlineData("A")]
    [InlineData("AB CD")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    [InlineData("")]
    public void Invalid_reference_prefixes_are_rejected(string prefix)
        => Assert.Throws<DomainException>(() => ReferenceNumberingRule.Create(prefix));

    [Fact]
    public void Reference_sequence_overflow_is_an_error_not_a_silent_wrap()
    {
        var rule = ReferenceNumberingRule.Create("ABC", "-", 3);
        Assert.Equal("ABC-999", rule.Format(999));
        Assert.Equal("sequence_overflow", Assert.Throws<DomainException>(() => rule.Format(1000)).Code);
        Assert.Throws<DomainException>(() => rule.Format(0));
    }

    [Fact]
    public void Organisation_validates_time_zone_culture_and_currency()
    {
        var rule = ReferenceNumberingRule.Create("DEMO");
        var org = Organisation.Create(Guid.NewGuid(), "Org", "Australia/Melbourne", "en-AU", "aud", rule, null, Now);
        Assert.Equal("AUD", org.DefaultCurrencyCode);
        Assert.Equal("DEMO", org.ReferenceRule.Prefix);
        Assert.Equal("invalid_time_zone", Assert.Throws<DomainException>(() => Organisation.Create(Guid.NewGuid(), "Org", "Mars/Olympus", "en-AU", "AUD", rule, null, Now)).Code);
        Assert.Equal("invalid_culture", Assert.Throws<DomainException>(() => Organisation.Create(Guid.NewGuid(), "Org", "Australia/Melbourne", "xx-NOPE", "AUD", rule, null, Now)).Code);
        Assert.Equal("invalid_currency", Assert.Throws<DomainException>(() => Organisation.Create(Guid.NewGuid(), "Org", "Australia/Melbourne", "en-AU", "AU", rule, null, Now)).Code);
        Assert.Throws<DomainException>(() => Organisation.Create(Guid.Empty, "Org", "Australia/Melbourne", "en-AU", "AUD", rule, null, Now));
    }

    [Fact]
    public void Tenant_starts_active_and_can_be_suspended()
    {
        var t = Tenant.Create("T", TenancyMode.SingleOrganisation, EcpMode.Demo, Now);
        Assert.Equal(TenantStatus.Active, t.Status);
        t.Suspend();
        Assert.Equal(TenantStatus.Suspended, t.Status);
        Assert.Throws<DomainException>(() => Tenant.Create(" ", TenancyMode.SingleOrganisation, EcpMode.Demo, Now));
    }
}

public class ContractDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    private static readonly Guid T = Guid.NewGuid(), O = Guid.NewGuid(), S = Guid.NewGuid(), C = Guid.NewGuid();

    private static NewContract New(string reference = "jbsaus-000001", string title = "Supply agreement", decimal? value = null, string? currency = null,
        DateOnly? start = null, DateOnly? end = null, string classification = Classifications.Internal)
        => new(T, O, reference, title, S, C, "Draft", "tester", Now, CurrencyCode: currency, EstimatedValue: value, StartDate: start, EndDate: end, Classification: classification);

    [Fact]
    public void Draft_contract_normalises_the_reference_and_starts_unlocked()
    {
        var c = Contract.CreateDraft(New("  jbsaus-000001 "));
        Assert.Equal("JBSAUS-000001", c.AgreementReference);
        Assert.Equal("Draft", c.Status);
        Assert.False(c.RecordLocked);
        Assert.False(c.IsLegacyLoad);
        Assert.Null(c.RegisteredDateUtc);
    }

    [Fact]
    public void Contract_requires_a_currency_with_a_value_and_rejects_negative_values()
    {
        Assert.Equal("currency_required", Assert.Throws<DomainException>(() => Contract.CreateDraft(New(value: 100m))).Code);
        Assert.Equal("invalid_value", Assert.Throws<DomainException>(() => Contract.CreateDraft(New(value: -1m, currency: "AUD"))).Code);
        Assert.Equal("invalid_currency", Assert.Throws<DomainException>(() => Contract.CreateDraft(New(value: 1m, currency: "AU"))).Code);
        Assert.Equal("AUD", Contract.CreateDraft(New(value: 125000m, currency: "aud")).CurrencyCode);
    }

    [Fact]
    public void Contract_rejects_an_end_date_before_the_start_date_and_unknown_classification()
    {
        Assert.Equal("invalid_dates", Assert.Throws<DomainException>(() => Contract.CreateDraft(New(start: new DateOnly(2026, 2, 1), end: new DateOnly(2026, 1, 1)))).Code);
        Assert.Equal("invalid_classification", Assert.Throws<DomainException>(() => Contract.CreateDraft(New(classification: "Top Secret"))).Code);
    }

    [Fact]
    public void Draft_details_can_be_edited_but_overlong_values_are_rejected_not_truncated()
    {
        var c = Contract.CreateDraft(New());
        c.UpdateDetails("New title", "Description", "tester2", Now.AddMinutes(1));
        Assert.Equal("New title", c.ContractTitle);
        Assert.Equal("tester2", c.UpdatedBy);
        Assert.Equal("too_long", Assert.Throws<DomainException>(() => c.UpdateDetails(new string('x', 501), null, "t", Now)).Code);
        Assert.Equal("New title", c.ContractTitle);
    }

    [Fact]
    public void Workflow_state_that_locks_makes_the_record_immutable_for_every_caller()
    {
        var c = Contract.CreateDraft(New());
        c.ApplyWorkflowState("Effective", locks: true, "approver", Now);
        Assert.True(c.RecordLocked);
        Assert.Equal(Now, c.LockedUtc);
        var ex = Assert.Throws<DomainException>(() => c.UpdateDetails("Changed", null, "anyone", Now));
        Assert.Equal("record_locked", ex.Code);
        Assert.Equal("Supply agreement", c.ContractTitle);
    }

    [Fact]
    public void Registered_date_is_stamped_once_and_independent_of_effective_date()
    {
        var c = Contract.CreateDraft(new NewContract(T, O, "A-1", "t", S, C, "Draft", "u", Now, EffectiveDate: new DateOnly(2024, 1, 1)));
        c.StampRegistration("group.head", Now);
        Assert.Equal(Now, c.RegisteredDateUtc);
        Assert.Equal(RegisteredDateProvenance.Platform, c.RegisteredDateProvenance);
        Assert.Equal(new DateOnly(2024, 1, 1), c.EffectiveDate);
        Assert.Equal("already_registered", Assert.Throws<DomainException>(() => c.StampRegistration("someone.else", Now.AddDays(1))).Code);
        Assert.Equal("group.head", c.RegisteredBy);
    }

    private static Contract Legacy(bool allowRegistered, DateTime? registered = null)
        => Contract.CreateLegacyLoad(New("DEMO-AGR-0001"), "Effective", new SourceProvenance("Medius", "MED-1001", "DEMO-AGR-0001", "Effective", Now),
            new LegacyLoadAuthorisation(Guid.NewGuid(), "migration.admin", allowRegistered), registered, "2024-01-15");

    [Fact]
    public void Legacy_load_is_locked_flagged_and_keeps_source_provenance()
    {
        var c = Legacy(allowRegistered: true, registered: new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(c.IsLegacyLoad);
        Assert.True(c.RecordLocked);
        Assert.Equal("MED-1001", c.SourceRecordId);
        Assert.Equal("Medius", c.SourceSystem);
        Assert.Equal("DEMO-AGR-0001", c.SourceAgreementReference);
        Assert.NotNull(c.MigrationBatchId);
        Assert.Equal(RegisteredDateProvenance.Migration, c.RegisteredDateProvenance);
        Assert.Equal("2024-01-15", c.SourceRegisteredValue);
        Assert.Equal("migration.admin", c.RegisteredBy);
        Assert.Equal("record_locked", Assert.Throws<DomainException>(() => c.UpdateDetails("x", null, "u", Now)).Code);
    }

    [Fact]
    public void Registered_date_is_imported_only_when_the_policy_allows_it_and_never_fabricated()
    {
        var denied = Legacy(allowRegistered: false, registered: new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(denied.RegisteredDateUtc);
        Assert.Null(denied.RegisteredDateProvenance);
        var absent = Legacy(allowRegistered: true, registered: null);
        Assert.Null(absent.RegisteredDateUtc);
    }

    [Fact]
    public void Legacy_registered_date_cannot_be_overwritten_by_a_later_registration()
    {
        var c = Legacy(true, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("already_registered", Assert.Throws<DomainException>(() => c.StampRegistration("x", Now)).Code);
    }

    [Fact]
    public void Workflow_controlled_members_have_no_public_setters()
    {
        foreach (var name in new[] { nameof(Contract.Status), nameof(Contract.RecordLocked), nameof(Contract.RegisteredDateUtc), nameof(Contract.RegisteredBy),
                     nameof(Contract.IsLegacyLoad), nameof(Contract.AgreementReference), nameof(Contract.TenantId) })
        {
            var setter = typeof(Contract).GetProperty(name)!.SetMethod;
            Assert.True(setter is null || !setter.IsPublic, $"{name} must not have a public setter");
        }
    }
}

public class SupplierAndCategoryTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Southern Cross  Demo Supplies Pty Ltd", "SOUTHERN CROSS DEMO SUPPLIES PTY LTD")]
    [InlineData("  demo energy markets pty ltd ", "DEMO ENERGY MARKETS PTY LTD")]
    [InlineData("Ａｃｍｅ", "ACME")]
    public void Supplier_names_normalise_for_exact_matching(string input, string expected)
        => Assert.Equal(expected, Supplier.NormaliseName(input));

    [Theory]
    [InlineData("51 824 753 556", "51824753556")]
    [InlineData("51824753556", "51824753556")]
    public void Valid_abns_are_normalised_to_digits(string abn, string expected)
        => Assert.Equal(expected, Supplier.NormaliseAbn(abn));

    [Theory]
    [InlineData("1234")]
    [InlineData("5182475355A")]
    [InlineData("518247535567")]
    public void Invalid_abns_are_rejected(string abn)
        => Assert.Equal("invalid_abn", Assert.Throws<DomainException>(() => Supplier.NormaliseAbn(abn)).Code);

    [Fact]
    public void Supplier_creation_requires_a_name_and_a_valid_tenant()
    {
        var s = Supplier.Create(Guid.NewGuid(), Guid.NewGuid(), "Acme Pty Ltd", null, "900001", Now);
        Assert.Equal("ACME PTY LTD", s.NormalisedName);
        Assert.Throws<DomainException>(() => Supplier.Create(Guid.Empty, Guid.NewGuid(), "x", null, null, Now));
        Assert.Throws<DomainException>(() => Supplier.Create(Guid.NewGuid(), Guid.NewGuid(), " ", null, null, Now));
    }

    [Fact]
    public void Category_codes_are_upper_case_and_required()
    {
        Assert.Equal("PACKAGING-FLEX", Category.Create(Guid.NewGuid(), Guid.NewGuid(), "packaging-flex", "Flexible Packaging", Guid.NewGuid()).Code);
        Assert.Throws<DomainException>(() => Category.Create(Guid.NewGuid(), Guid.NewGuid(), "", "x"));
    }
}
