using System.Diagnostics;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Planning;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Migration")]
public class MappingEngineTests
{
    private static readonly MappingContext Context = new(TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane"), SourceIsSpreadsheet: false);

    private static FieldMapping Map(string column, string target, string transformation = "Trim", string? def = null, bool required = false, string? lookup = null, string? rules = null)
        => new(column, target, transformation, def, required, lookup, rules, null);

    private static MappedRow Run(FieldMapping mapping, string value, IReadOnlyList<LookupEntry>? lookups = null, MappingContext? context = null)
        => MappingEngine.MapRow(new MappingDefinition([mapping], lookups ?? [], new ProfileOptions()), new Dictionary<string, string> { [mapping.SourceColumn] = value }, context ?? Context);

    [Fact]
    public void Trim_and_case_transformations_normalise_and_say_so()
    {
        var r = Run(Map("T", "contractTitle", "Trim+NormaliseWhitespace"), "  Hello   world  ");
        Assert.Equal("Hello world", r.Values["contractTitle"]);
        Assert.Contains(r.Messages, m => m.Code == "MIG-W001" && !m.IsError);
        Assert.Equal("AUD", Run(Map("C", "currencyCode", "Trim+Upper"), "aud").Values["currencyCode"]);
        Assert.Equal("Southern Cross", Run(Map("N", "description", "TitleCase"), "SOUTHERN CROSS").Values["description"]);
    }

    [Fact] // MIG-014
    public void Each_value_records_its_original_and_transformed_form_and_whether_it_changed()
    {
        var r = Run(Map("C", "currencyCode", "Upper", def: "AUD"), "aud");
        var t = Assert.Single(r.Trace);
        Assert.Equal(("aud", "AUD", true), (t.Original, t.Transformed, t.Changed));
        var empty = Run(Map("C", "currencyCode", "Upper", def: "AUD"), "");
        Assert.True(empty.Trace[0].DefaultApplied);
        Assert.Equal("AUD", empty.Values["currencyCode"]);
        Assert.Contains(empty.Messages, m => m.Code == "MIG-W002");
    }

    [Fact] // MIG-010
    public void Dates_need_an_explicit_format_and_an_ambiguous_day_month_order_is_never_guessed()
    {
        var iso = Run(Map("D", "startDate", "ParseDate", rules: "Format=yyyy-MM-dd"), "2024-01-02");
        Assert.Equal("2024-01-02", iso.Values["startDate"]);
        Assert.Equal("2024-02-01", Run(Map("D", "startDate", "ParseDate", rules: "Format=dd/MM/yyyy"), "01/02/2024").Values["startDate"]);
        Assert.Equal("2024-01-02", Run(Map("D", "startDate", "ParseDate", rules: "Format=MM/dd/yyyy"), "01/02/2024").Values["startDate"]);
        var wrong = Run(Map("D", "startDate", "ParseDate", rules: "Format=yyyy-MM-dd"), "01/02/2024");
        Assert.Contains(wrong.Messages, m => m.Code == "MIG-E002" && m.IsError);
        Assert.DoesNotContain("startDate", wrong.Values.Keys);
        // A profile that omits the format cannot be published.
        var issues = MappingDefinitionValidator.Validate(Definition(Map("D", "startDate", "ParseDate")));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("explicit Format"));
    }

    [Fact]
    public void A_spreadsheet_date_arrives_as_an_ISO_date_and_is_accepted_whatever_the_declared_format()
    {
        var sheet = new MappingContext(Context.OrganisationTimeZone, SourceIsSpreadsheet: true);
        Assert.Equal("2024-01-15", Run(Map("D", "startDate", "ParseDate", rules: "Format=dd/MM/yyyy"), "2024-01-15", context: sheet).Values["startDate"]);
        Assert.Contains(Run(Map("D", "startDate", "ParseDate", rules: "Format=dd/MM/yyyy"), "2024-01-15").Messages, m => m.Code == "MIG-E002"); // a CSV is not given that latitude
    }

    [Fact]
    public void Date_times_become_UTC_using_the_organisation_zone_and_daylight_saving_gaps_are_errors()
    {
        Assert.Equal("2024-01-14T14:00:00Z", Run(Map("R", "registeredDate", "ParseDateTime", rules: "Format=yyyy-MM-dd;TimeZone=organisation"), "2024-01-15").Values["registeredDate"]);
        Assert.Equal("2024-01-15T00:00:00Z", Run(Map("R", "registeredDate", "ParseDateTime", rules: "Format=yyyy-MM-dd;TimeZone=UTC"), "2024-01-15").Values["registeredDate"]);
        var melbourne = new MappingContext(TimeZoneInfo.FindSystemTimeZoneById("Australia/Melbourne"), false);
        var gap = Run(Map("R", "registeredDate", "ParseDateTime", rules: "Format=yyyy-MM-dd HH:mm"), "2024-10-06 02:30", context: melbourne); // clocks go forward at 02:00
        Assert.Contains(gap.Messages, m => m.Code == "MIG-E002");
    }

    [Fact]
    public void Numbers_must_parse_completely_and_booleans_must_be_in_the_listed_sets()
    {
        Assert.Equal("125000.50", Run(Map("V", "estimatedValue", "ParseDecimal"), "125000.50").Values["estimatedValue"]);
        Assert.Equal("1250.5", Run(Map("V", "estimatedValue", "ParseDecimal", rules: "ThousandsSeparator=.;DecimalSeparator=,"), "1.250,5").Values["estimatedValue"]);
        Assert.Contains(Run(Map("V", "estimatedValue", "ParseDecimal"), "$125,000x").Messages, m => m.Code == "MIG-E003");
        Assert.Equal("true", Run(Map("L", "legalReviewRequired", "MapBoolean", rules: "True=Yes;Y;TRUE;1|False=No;N;FALSE;0"), "y").Values["legalReviewRequired"]);
        Assert.Equal("false", Run(Map("L", "legalReviewRequired", "MapBoolean", rules: "True=Yes;Y;TRUE;1|False=No;N;FALSE;0"), "0").Values["legalReviewRequired"]);
        Assert.Contains(Run(Map("L", "legalReviewRequired", "MapBoolean", rules: "True=Yes;Y;TRUE;1|False=No;N;FALSE;0"), "maybe").Messages, m => m.Code == "MIG-E004");
    }

    [Fact]
    public void Lookups_map_values_and_the_missing_behaviour_is_error_default_or_skip()
    {
        var lookups = new[] { new LookupEntry("status", "Expired", "Ended", null), new LookupEntry("status", "Effective", "Effective", null) };
        Assert.Equal("Ended", Run(Map("S", "status", "LookupMap", lookup: "status"), " expired ", lookups).Values["status"]);
        var missing = Run(Map("S", "status", "LookupMap", lookup: "status"), "Pending", lookups);
        Assert.Contains(missing.Messages, m => m.Code == "MIG-E004" && m.IsError);
        Assert.Equal("Draft", Run(Map("S", "status", "LookupMap", lookup: "status", rules: "OnMissing=Default;Default=Draft"), "Pending", lookups).Values["status"]);
        var skipped = Run(Map("S", "status", "LookupMap", lookup: "status", rules: "OnMissing=Skip"), "Pending", lookups);
        Assert.Empty(skipped.Values); Assert.Empty(skipped.Messages);
    }

    [Fact] // MIG-020
    public void A_value_longer_than_its_limit_is_an_error_and_is_never_cut()
    {
        var r = Run(Map("T", "contractTitle", rules: "MaxLength=10"), "This title is far too long");
        Assert.Contains(r.Messages, m => m.Code == "MIG-E009" && m.IsError);
        Assert.DoesNotContain("contractTitle", r.Values.Keys);
        Assert.Equal("exact", Run(Map("T", "contractTitle", rules: "MaxLength=5;MinLength=5"), "exact").Values["contractTitle"]);
        Assert.Contains(Run(Map("C", "currencyCode", rules: "Length=3"), "AU").Messages, m => m.Code == "MIG-E015");
        Assert.Contains(Run(Map("V", "estimatedValue", "ParseDecimal", rules: "Min=0"), "-5").Messages, m => m.Code == "MIG-E015");
    }

    [Fact]
    public void Required_values_concat_and_split_work_and_a_missing_required_value_is_MIG_E001()
    {
        Assert.Contains(Run(Map("T", "contractTitle", required: true), "   ").Messages, m => m.Code == "MIG-E001");
        var concat = MappingEngine.MapRow(new MappingDefinition([Map("A", "contractTitle", "Concat", rules: "Columns=A;B;Separator= - ")], [], new ProfileOptions()),
            new Dictionary<string, string> { ["A"] = "Part one", ["B"] = "Part two" }, Context);
        Assert.Equal("Part one - Part two", concat.Values["contractTitle"]);
        Assert.Equal("SUP", Run(Map("R", "sourceRecordId", "Split", rules: "Separator=/;Index=1"), "A/SUP/C").Values["sourceRecordId"]);
    }

    [Fact]
    public void Parameters_parse_value_lists_and_report_unknown_keys()
    {
        var p = MappingParameters.Parse("True=Yes;Y;1|False=No;N;0");
        Assert.Equal(["Yes", "Y", "1"], p.GetList("True")); Assert.Equal(["No", "N", "0"], p.GetList("False"));
        var q = MappingParameters.Parse("Format=yyyy-MM-dd;TimeZone=organisation;Colour=red");
        Assert.Equal("yyyy-MM-dd", q.Get("Format"));
        Assert.Contains(q.Problems, x => x.Contains("Colour"));
        Assert.Contains(MappingParameters.Parse("loose").Problems, x => x.Contains("loose")); // a bare word that continues no list
    }

    private static MappingDefinition Definition(params FieldMapping[] fields) => new(fields, [], new ProfileOptions());

    [Fact]
    public void A_profile_is_checked_as_a_whole_before_it_can_be_published()
    {
        var good = ProfileFromSamples();
        Assert.DoesNotContain(MappingDefinitionValidator.Validate(good), i => i.IsError);

        var issues = MappingDefinitionValidator.Validate(new MappingDefinition([
            Map("A", "custom.region"), Map("B", "unknown.field"), Map("C", "contractTitle", "Eval"), Map("D", "contractTitle"), Map("E", "status", "LookupMap"),
            Map("F", "startDate", "ParseDate", rules: "Format=yyyy-MM-dd;Bogus=1")], [new("x", "A", "1", null), new("x", " a ", "2", null)], new ProfileOptions()));
        string[] expected = ["Custom fields are not available", "is not a field a migration can set", "is not a safe transformation", "mapped more than once", "needs a LookupMapKey", "is not a known parameter",
            "must map 'sourceRecordId'", "appears more than once"];
        foreach (var e in expected) Assert.Contains(issues, i => i.IsError && i.Message.Contains(e) || i.IsError && i.Field.Contains(e));
    }

    public static MappingDefinition ProfileFromSamples()
    {
        var fields = ProfileCsv.ParseFieldMappings(MigrationSamples.Open("field-mapping-profile-complete.csv"), out var p1);
        var lookups = ProfileCsv.ParseLookups(MigrationSamples.Open("status-lookup-map.csv"), out var p2);
        Assert.Empty(p1); Assert.Empty(p2);
        return new MappingDefinition(fields, lookups, new ProfileOptions(false, null, ["Evergreen"]));
    }

    [Fact] // MIG-014 / MIG-018 groundwork: the shipped sample maps cleanly
    public void The_repository_sample_profile_has_eighteen_mappings_and_maps_both_sample_rows_without_a_problem()
    {
        var d = ProfileFromSamples();
        Assert.Equal(18, d.Fields.Count);
        Assert.Equal(10, d.Lookups.Count);
        var table = Ecp.Formats.CsvReader.Read(new MemoryStream(MigrationSamples.ContractsCsv));
        foreach (var row in table.Rows)
        {
            var mapped = MappingEngine.MapRow(d, table.ToMap(row), Context);
            Assert.DoesNotContain(mapped.Messages, m => m.IsError);
            Assert.Equal("Effective", mapped.Values["status"]);
            Assert.Equal("true", mapped.Values["legalReviewRequired"]);
            Assert.Equal("AUD", mapped.Values["currencyCode"]);
        }
        var first = MappingEngine.MapRow(d, table.ToMap(table.Rows[0]), Context);
        Assert.Equal("PACKAGING", first.Values["category.name"]); Assert.Equal("PACKAGING-FLEX", first.Values["subcategory.name"]);
        Assert.Equal("2024-01-14T14:00:00Z", first.Values["registeredDate"]);
        var second = MappingEngine.MapRow(d, table.ToMap(table.Rows[1]), Context);
        Assert.DoesNotContain("endDate", second.Values.Keys); // blank end date for an evergreen agreement
        Assert.Equal("Evergreen", second.Values["extensionType"]);
    }

    [Fact] // MIG-019
    public void A_naming_rule_runs_on_the_safe_engine_so_a_hostile_pattern_cannot_stall_a_batch()
    {
        Assert.NotNull(NamingRule.Compile("(?<agreementReference>[A-Z]+-[0-9]+)/", out _));
        Assert.Null(NamingRule.Compile("(?<agreementReference>(?=a)a+)", out var lookahead)); Assert.Contains("not a valid pattern", lookahead);   // look-around needs a backtracking engine
        Assert.Null(NamingRule.Compile("([a-z]+)", out var noGroup)); Assert.Contains("must contain a group", noGroup);
        Assert.Null(NamingRule.Compile(new string('a', 400), out var longRule)); Assert.Contains("300", longRule);
        var hostile = NamingRule.Compile("(?<agreementReference>(a+)+)$", out _)!;
        var sw = Stopwatch.StartNew();
        var hit = hostile.Match(new string('a', 50_000) + "!");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "a pattern that is catastrophic on a backtracking engine must finish quickly here");
        _ = hit;
    }
}

[Trait("Category", "Migration")]
public class AttachmentMatcherTests
{
    private static readonly Guid R1 = Guid.Parse("00000000-0000-0000-0000-000000000011"), R2 = Guid.Parse("00000000-0000-0000-0000-000000000012"), R3 = Guid.Parse("00000000-0000-0000-0000-000000000013");
    private static MatchRow Row(Guid id, int seq, string source, string reference, string? folder = null, bool eligible = true, bool rejected = false) => new(id, seq, source, reference, folder, eligible, rejected);

    private static (List<MatchFile> Files, List<MatchEntry> Entries, List<MatchRow> Rows) Data()
    {
        var f1 = new MatchFile(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "DEMO-AGR-0001/a_executed.pdf");
        var f2 = new MatchFile(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "DEMO-AGR-0002/b.pdf");
        var f3 = new MatchFile(Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "loose/c.pdf");
        var f4 = new MatchFile(Guid.Parse("00000000-0000-0000-0000-0000000000a4"), "DEMO-AGR-0003/d.pdf");
        var e1 = new MatchEntry(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), "DEMO-AGR-0001/a_executed.pdf", "MED-1001", "DEMO-AGR-0001");
        var e2 = new MatchEntry(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "DEMO-AGR-0002/b.pdf", null, "demo-agr-0002");
        var e3 = new MatchEntry(Guid.Parse("00000000-0000-0000-0000-0000000000b3"), "missing/not-there.pdf", "MED-1001", null);
        return ([f1, f2, f3, f4], [e1, e2, e3], [Row(R1, 1, "MED-1001", "DEMO-AGR-0001"), Row(R2, 2, "MED-1002", "DEMO-AGR-0002"), Row(R3, 3, "MED-1003", "DEMO-AGR-0003")]);
    }

    [Fact]
    public void Methods_apply_in_the_documented_order_with_a_reason_for_each_match()
    {
        var (files, entries, rows) = Data();
        var result = AttachmentMatcher.Match(files, entries, rows, null);
        var byPath = result.Files.ToDictionary(m => files.Single(f => f.Id == m.FileId).RelativePath);
        Assert.Equal((MatchOutcome.Matched, MatchMethod.SourceRecordId, R1), (byPath["DEMO-AGR-0001/a_executed.pdf"].Outcome, byPath["DEMO-AGR-0001/a_executed.pdf"].Method, byPath["DEMO-AGR-0001/a_executed.pdf"].RowId));
        Assert.Contains("MED-1001", byPath["DEMO-AGR-0001/a_executed.pdf"].Candidates[0].Reason);
        Assert.Equal((MatchOutcome.Matched, MatchMethod.AgreementReference, R2), (byPath["DEMO-AGR-0002/b.pdf"].Outcome, byPath["DEMO-AGR-0002/b.pdf"].Method, byPath["DEMO-AGR-0002/b.pdf"].RowId)); // case-insensitive exact reference
        Assert.Equal(MatchOutcome.NoMatch, byPath["loose/c.pdf"].Outcome);
        Assert.Contains(byPath["loose/c.pdf"].Messages, m => m.Code == "MIG-F010"); // no manifest row and no match
        Assert.Equal((MatchOutcome.Matched, MatchMethod.FolderName, R3), (byPath["DEMO-AGR-0003/d.pdf"].Outcome, byPath["DEMO-AGR-0003/d.pdf"].Method, byPath["DEMO-AGR-0003/d.pdf"].RowId)); // exact parent folder
        Assert.Single(result.OrphanEntries); // the manifest row naming a file that is not there (MIG-F009)
    }

    [Fact] // MIG-003
    public void The_same_inputs_in_any_order_give_exactly_the_same_proposals()
    {
        var (files, entries, rows) = Data();
        string Describe(MatchResult r) => string.Join(";", r.Files.OrderBy(f => f.FileId).Select(f => $"{f.FileId}:{f.Outcome}:{f.Method}:{f.RowId}:{string.Join(",", f.Candidates.Select(c => c.RowId + c.Method.ToString()))}")) + "|" + string.Join(",", r.OrphanEntries.Select(o => o.EntryId));
        var baseline = Describe(AttachmentMatcher.Match(files, entries, rows, null));
        var rng = new Random(12345);
        for (var i = 0; i < 25; i++)
            Assert.Equal(baseline, Describe(AttachmentMatcher.Match(files.OrderBy(_ => rng.Next()).ToList(), entries.OrderBy(_ => rng.Next()).ToList(), rows.OrderBy(_ => rng.Next()).ToList(), null)));
    }

    [Fact] // MIG-003
    public void Ties_are_never_broken_automatically_and_conflicting_keys_go_to_a_person()
    {
        var file = new MatchFile(Guid.NewGuid(), "shared/x.pdf");
        // Two contracts share a folder name: ambiguous, with both offered.
        var rows = new List<MatchRow> { Row(R1, 1, "A", "REF-1", folder: "shared"), Row(R2, 2, "B", "REF-2", folder: "shared") };
        var folderTie = AttachmentMatcher.Match([file], [], rows, null).Files.Single();
        Assert.Equal(MatchOutcome.Ambiguous, folderTie.Outcome);
        Assert.Equal(2, folderTie.Candidates.Count); Assert.Null(folderTie.RowId);
        // The manifest's SourceRecordId and AgreementReference point at different contracts: a conflict, not a choice.
        var entry = new MatchEntry(Guid.NewGuid(), "shared/x.pdf", "A", "REF-2");
        var conflict = AttachmentMatcher.Match([file], [entry], rows, null).Files.Single();
        Assert.Equal(MatchOutcome.Ambiguous, conflict.Outcome); Assert.Contains(conflict.Messages, m => m.Code == "MIG-F012");
        // A duplicate source record is never a candidate, so the first occurrence is the one matched.
        var dupRows = new List<MatchRow> { Row(R1, 1, "A", "REF-1"), Row(R2, 2, "A", "REF-1", eligible: false) };
        Assert.Equal(R1, AttachmentMatcher.Match([new MatchFile(Guid.NewGuid(), "p/y.pdf")], [new MatchEntry(Guid.NewGuid(), "p/y.pdf", "A", null)], dupRows, null).Files.Single().RowId);
        // Two manifest rows naming one path cannot be paired.
        var twice = AttachmentMatcher.Match([file], [new MatchEntry(Guid.NewGuid(), "shared/x.pdf", "A", null), new MatchEntry(Guid.NewGuid(), "SHARED/x.pdf", "B", null)], rows, null).Files.Single();
        Assert.Equal(MatchOutcome.Ambiguous, twice.Outcome);
    }

    [Fact]
    public void A_naming_rule_matches_exactly_and_a_file_for_a_rejected_contract_is_held()
    {
        var rule = NamingRule.Compile("^archive/(?<agreementReference>[A-Z]+-[0-9]+)_", out _);
        var rows = new List<MatchRow> { Row(R1, 1, "A", "AGR-77"), Row(R2, 2, "B", "AGR-78", rejected: true) };
        var ok = AttachmentMatcher.Match([new MatchFile(Guid.NewGuid(), "archive/AGR-77_executed.pdf")], [], rows, rule).Files.Single();
        Assert.Equal((MatchOutcome.Matched, MatchMethod.NamingRule, R1), (ok.Outcome, ok.Method, ok.RowId));
        var held = AttachmentMatcher.Match([new MatchFile(Guid.NewGuid(), "archive/AGR-78_executed.pdf")], [], rows, rule).Files.Single();
        Assert.Equal(MatchOutcome.TargetRejected, held.Outcome); Assert.Contains(held.Messages, m => m.Code == "MIG-F015");
    }
}

[Trait("Category", "Migration")]
public class DocumentPlannerTests
{
    private static DocFile File(string path, string hash, Guid row, Guid? contract = null, string? source = null, bool released = false) => new(Guid.NewGuid(), path, hash, row, contract, source ?? "src/" + path, released);
    private static readonly Guid RowA = Guid.NewGuid(), RowB = Guid.NewGuid();

    [Fact] // MIG-025
    public void Each_duplicate_option_behaves_as_specified()
    {
        DocDecision[] Plan(DocumentDuplicateOption option, params DocFile[] files) => DocumentPlanner.Plan(files, option, [], []).OrderBy(d => files.First(f => f.FileId == d.FileId).Path, StringComparer.Ordinal).ToArray();
        var a1 = File("a/1.pdf", "h1", RowA); var a2 = File("a/2.pdf", "h1", RowA); var b1 = File("b/1.pdf", "h1", RowB);

        var reject = Plan(DocumentDuplicateOption.RejectDuplicateHash, a1, a2);
        Assert.Equal([DocAction.ImportNew, DocAction.RejectDuplicate], reject.Select(d => d.Action)); Assert.True(reject[1].IsError);

        var link = Plan(DocumentDuplicateOption.LinkExisting, a1, a2, b1);
        Assert.Equal([DocAction.ImportNew, DocAction.LinkBatchDocument, DocAction.ImportNew], link.Select(d => d.Action)); // same contract linked; another contract keeps its own document

        var version = Plan(DocumentDuplicateOption.AddVersion, a1, a2);
        Assert.Equal([DocAction.ImportNew, DocAction.AddVersionToBatchDocument], version.Select(d => d.Action));
        Assert.Equal(a1.FileId, version[1].OriginalFileId);

        var separate = Plan(DocumentDuplicateOption.KeepSeparateWithWarning, a1, a2);
        Assert.Equal([DocAction.ImportNew, DocAction.ImportNew], separate.Select(d => d.Action)); Assert.Equal("MIG-F007", separate[1].MessageCode); Assert.False(separate[1].IsError);

        var manual = Plan(DocumentDuplicateOption.ManualReview, a1, a2);
        Assert.Equal([DocAction.ImportNew, DocAction.Hold], manual.Select(d => d.Action));
        // Once a person has released the held file, it imports as its own document.
        Assert.Equal(DocAction.ImportNew, DocumentPlanner.Plan([a1, File("a/2.pdf", "h1", RowA, released: true)], DocumentDuplicateOption.ManualReview, [], []).Last().Action);
    }

    [Fact]
    public void Content_already_in_the_target_and_documents_already_imported_are_recognised()
    {
        var existingDoc = Guid.NewGuid(); var contract = Guid.NewGuid();
        var f = File("a/1.pdf", "h1", RowA, contract, "S/1");
        var byHash = new[] { new ExistingDoc(existingDoc, contract, "other", "h1") };
        Assert.Equal(DocAction.AddVersionToExisting, DocumentPlanner.Plan([f], DocumentDuplicateOption.AddVersion, [], byHash).Single().Action);
        Assert.Equal(DocAction.LinkExisting, DocumentPlanner.Plan([f], DocumentDuplicateOption.LinkExisting, [], byHash).Single().Action);
        Assert.Equal(DocAction.ImportNew, DocumentPlanner.Plan([f with { ContractId = Guid.NewGuid() }], DocumentDuplicateOption.AddVersion, [], byHash).Single().Action); // a different contract
        // The same document source id: same content is a no-op (idempotent re-run), changed content is a new version.
        Assert.Equal(DocAction.AlreadyImported, DocumentPlanner.Plan([f], DocumentDuplicateOption.AddVersion, [new ExistingDoc(existingDoc, contract, "S/1", "h1")], []).Single().Action);
        Assert.Equal(DocAction.AddVersionToExisting, DocumentPlanner.Plan([f], DocumentDuplicateOption.RejectDuplicateHash, [new ExistingDoc(existingDoc, contract, "S/1", "different")], []).Single().Action);
    }
}
