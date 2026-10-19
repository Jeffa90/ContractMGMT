using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Lifecycle.Definitions;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Net.Http.Headers;
using static Ecp.Api.Tests.NoCodeRehearsal;

namespace Ecp.Api.Tests;

/// <summary>
/// The rehearsal itself: twenty-three steps from "a company has asked for an organisation" to "its register is loaded, checked and backed up", each done by the role that would do it, through the application's pages. See
/// docs/NO_CODE_REHEARSAL.md for what each step proves and docs/IMPLEMENTATION_PLAYBOOK.md for the journey it follows.
/// </summary>
internal static class NoCodeJourney
{
    /// <summary>The host the journey runs against: Test mode with the Production-equivalent second-person rule switched on by the explicit setting, the labelled mock scanner chosen on purpose, and (where the environment provides them) the shared backup folders.</summary>
    public static void Settings(Dictionary<string, string?> s)
    {
        s["Ecp:Mode"] = "Test";
        s[GovernanceMode.SettingKey] = "Enforced";
        s["Documents:Scanner:Provider"] = "mock";
        var host = Environment.GetEnvironmentVariable("ECP_TEST_SQL_BACKUP_DIR");
        var server = Environment.GetEnvironmentVariable("ECP_TEST_SQL_BACKUP_DIR_SERVER");
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(server))
        {
            s["Ecp:Recovery:BackupDirectory"] = host;
            s["Ecp:Recovery:SqlServerBackupDirectory"] = server;
            s["Ecp:Recovery:RestoreTargetDirectory"] = Path.Combine(host, "restore-target");
        }
    }

    public static async Task<RehearsalLedger> RunAsync(DemoApp app)
    {
        var ledger = new RehearsalLedger();
        var key = "RH" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var name = "Rehearsal Company " + key;
        Guid org = Guid.Empty;

        using var tenantAdmin = await app.SignInAsync(TenantAdmin);
        using var orgAdmin = await app.SignInAsync(OrgAdmin);
        using var bizConfig = await app.SignInAsync(BizConfig);
        using var procurement = await app.SignInAsync(ProcAdmin);
        using var procurementSecond = await app.SignInAsync(ProcAdmin2);
        using var sysAdmin = await app.SignInAsync(SysAdmin);
        using var smtp = new FakeSmtpServer();   // a real mail server on a loopback socket, standing in for the company's mail relay

        // The installation requires a second person to publish, exactly as Production does: the host was started in Test mode with Ecp:Governance:SecondPerson=Enforced (nothing here switches the rule on by hand).
        Assert.Equal(GovernanceKind.TestEnforced, app.Factory.Services.GetRequiredService<GovernanceMode>().Kind);
        Assert.True(app.Factory.Services.GetRequiredService<ConfigurationOptions>().RequireSecondPersonPublish);

        // 1. The Tenant Administrator makes sure the installation can hold several organisations.
        await ledger.Step(1, "Allow several organisations (only if the installation was set up for one)", "Tenant Administrator", "TenantAdministrator", "/Admin/Organisations", async () =>
        {
            var page = await GetAsync(tenantAdmin, "/Admin/Organisations");
            if (!page.Contains("supports one organisation")) return "Already supports several organisations.";
            ExpectDone(await Send(tenantAdmin, "/Admin/Organisations", "Widen", Fields(("Reason", "The rehearsal company joins this installation"))), "Widening");
            return "Widened once, with a reason; it cannot be narrowed.";
        });

        // 2. The organisation is created from the form: problems are shown beside the fields first.
        await ledger.Step(2, "Create the organisation: check the form, fix what it flags, create", "Tenant Administrator", "TenantAdministrator", "/Admin/Organisations/New", async () =>
        {
            var form = await GetAsync(tenantAdmin, "/Admin/Organisations/New");
            var adminId = Regex.Match(form, $"<option value=\"({Pattern})\"[^>]*>[^<]*{Regex.Escape(OrgAdmin)}").Groups[1].Value;
            Assert.NotEmpty(adminId);
            var packs = Regex.Matches(form, "<option value=\"([a-z0-9-]+)\"[^>]*>[^<]*\\(([a-z0-9-]+)\\)").Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("jbs-procurement", packs);
            var fields = Fields(("Name", name), ("LegalName", name + " Pty Ltd"), ("Key", key), ("Country", "AU"), ("Culture", "en-AU"), ("TimeZone", "Australia/Sydney"), ("Currency", "AUD"), ("ReferenceSeparator", "-"), ("ReferenceDigits", "6"),
                ("PackKey", "jbs-procurement"), ("InitialAdministratorId", adminId), ("StructureCsv", "type,key,name,parentKey,timeZone,registrationNumber\nDivision,OPS,Operations,,,\nBusinessUnit,FAC,Facilities,OPS,,\nSite,SYD,Sydney Office,,Australia/Sydney,")).ToList();

            // A first attempt with a locale and a currency nobody has heard of: nothing is created and each problem is beside its field.
            var wrong = fields.Where(f => f.Key is not ("Culture" or "Currency")).Concat(Fields(("Culture", "klingon"), ("Currency", "ZZZ"))).ToList();
            var checkedWrong = await Send(tenantAdmin, "/Admin/Organisations/New", "Check", wrong);
            Assert.Contains("Fix these first", checkedWrong.Body);
            Assert.Contains("That locale is not recognised", checkedWrong.Body);
            Assert.Contains("ISO 4217", checkedWrong.Body);
            Assert.DoesNotContain(name, await GetAsync(tenantAdmin, "/Admin/Organisations"));

            var ok = await Send(tenantAdmin, "/Admin/Organisations/New", "Check", fields);
            Assert.Contains("Ready to create", ok.Body);
            var created = await Send(tenantAdmin, "/Admin/Organisations/New", "Create", fields);
            Assert.Contains("The organisation was created", created.Body);
            org = FirstGuid(created.Body, $"/Admin/Organisations/Setup\\?id=({Pattern})");
            return $"Organisation {key} created with structure, an initial administrator and the pack choice; nothing published.";
        });

        // 3. The Tenant Administrator gives the other people their roles in the new organisation, from the Users page.
        await ledger.Step(3, "Give people their roles in the new organisation", "Tenant Administrator", "TenantAdministrator", "/Admin/Users/Details/{person}", async () =>
        {
            async Task Grant(string email, string role)
            {
                var (_, id) = await app.FindUserAsync(email);
                ExpectDone(await Send(tenantAdmin, $"/Admin/Users/Details/{id}", "Grant", Fields(("role", role), ("level", "Organisation"), ("organisations", org.ToString()), ("ceiling", "Confidential"), ("reason", "Rehearsal company roles"))), "Granting " + role);
            }
            await Grant(BizConfig, "BusinessConfigurationAdministrator");
            await Grant(ProcAdmin, "ProcurementAdministrator");
            await Grant(ProcAdmin2, "ProcurementAdministrator");
            return "One second configuration administrator and two procurement administrators were given their roles for this organisation only.";
        });

        // 4. The organisation's administrator reads the checklist and prepares the drafts from the pack.
        await ledger.Step(4, "Read the set-up checklist and prepare the drafts from the pack", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Organisations/Setup, /Admin/Packs/Assign", async () =>
        {
            var checklist = Words(await GetAsync(orgAdmin, $"/Admin/Organisations/Setup?id={org}"));
            Assert.Contains("Initial administrator", checklist);
            var assign = await GetAsync(orgAdmin, $"/Admin/Packs/Assign?organisationId={org}");
            var choice = Regex.Match(assign, "<option value=\"(jbs-procurement\\|[^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(choice);
            var preview = await GetAsync(orgAdmin, $"/Admin/Packs/Assign?organisationId={org}&Choice={Uri.EscapeDataString(choice)}");
            Assert.Contains("Assign this pack and prepare drafts", preview);
            var parts = choice.Split('|');
            var assigned = await Send(orgAdmin, $"/Admin/Packs/Assign?organisationId={org}", null, Fields(("OrganisationId", org.ToString()), ("Key", parts[0]), ("Version", parts[1])));
            Assert.Equal(HttpStatusCode.OK, assigned.Status);
            Assert.Contains("Draft created", assigned.Body);
            return "Drafts prepared from the pack: " + string.Join(", ", Regex.Matches(assigned.Body, "<li>(\\w+): Draft created").Select(m => m.Groups[1].Value));
        });

        // 5-7. Regional settings, terminology and capabilities: submitted by one person, published by another.
        await ledger.Step(5, "Regional settings: review, check and submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Regional", async () =>
        {
            var regional = Fields(("OrganisationId", org.ToString()), ("Country", "AU"), ("Locale", "en-AU"), ("SupportedLocales", ""), ("Currency", "AUD"), ("SupportedCurrencies", ""), ("TimeZone", "Australia/Sydney"),
                ("TimeDisplay", "24h"), ("PhoneRegion", "AU"), ("OwnIdentifiers", "false"), ("OwnAddress", "false"), ("Note", "Australian settings"));
            ExpectDone(await Send(orgAdmin, $"/Admin/Regional?organisationId={org}", "Validate", regional), "Regional check");
            var submitted = await Send(orgAdmin, $"/Admin/Regional?organisationId={org}", "Publish", regional);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Regional submit " + Words(submitted.Body)[..Math.Min(300, Words(submitted.Body).Length)]);
            return "Submitted for review (not in force).";
        });

        await ledger.Step(6, "Terminology: keep the pack's wording, submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Terminology/Edit", async () =>
        {
            var form = new List<KeyValuePair<string, string>> { new("OrganisationId", org.ToString()), new("Locale", "en"), new("Note", "Pack wording confirmed") };
            for (var i = 0; i < Ecp.SharedKernel.TermCatalogue.All.Count; i++) form.Add(new($"Rows[{i}].Key", Ecp.SharedKernel.TermCatalogue.All[i].Key));
            await Send(orgAdmin, $"/Admin/Terminology/Edit?organisationId={org}&locale=en", "Validate", form);
            var submitted = await Send(orgAdmin, $"/Admin/Terminology/Edit?organisationId={org}&locale=en", "Publish", form);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Terminology submit " + Words(submitted.Body)[..Math.Min(300, Words(submitted.Body).Length)]);
            return "Submitted for review (not in force).";
        });

        await ledger.Step(7, "Capabilities: confirm the pack's choices, submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Capabilities/Edit", async () =>
        {
            var form = new List<KeyValuePair<string, string>> { new("OrganisationId", org.ToString()), new("Note", "Pack choices confirmed") };
            for (var i = 0; i < CapabilityCatalogue.All.Count; i++) { form.Add(new($"Rows[{i}].Key", CapabilityCatalogue.All[i].Key)); form.Add(new($"Rows[{i}].Choice", "inherit")); }
            var submitted = await Send(orgAdmin, $"/Admin/Capabilities/Edit?organisationId={org}", "Publish", form);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Capabilities submit " + Words(submitted.Body)[..Math.Min(300, Words(submitted.Body).Length)]);
            return "Submitted for review (not in force).";
        });

        // 8. Approver groups, in forms.
        var groupKey = "Rehearsal" + key;
        await ledger.Step(8, "Approver group: register it and add its people", "Organisation Administrator", "OrganisationAdministrator", "/Admin/ApproverGroups, /Admin/ApproverGroups/Details", async () =>
        {
            ExpectDone(await Send(orgAdmin, "/Admin/ApproverGroups", "Create", Fields(("Key", groupKey), ("Description", "Approves rehearsal contracts"), ("OrganisationId", org.ToString()), ("AllowDelegation", "true"))), "Registering the group");
            var (_, member) = await app.FindUserAsync(BizConfig);
            var detail = $"/Admin/ApproverGroups/Details?key={groupKey}";
            var preview = await Send(orgAdmin, detail, "PreviewAdd", Fields(("Key", groupKey), ("UserId", member.ToString()), ("Role", "primary"), ("OrganisationId", org.ToString()), ("Ceiling", "Confidential")));
            Assert.Contains("approve", Words(preview.Body), StringComparison.OrdinalIgnoreCase);
            ExpectDone(await Send(orgAdmin, detail, "Add", Fields(("Key", groupKey), ("UserId", member.ToString()), ("Role", "primary"), ("OrganisationId", org.ToString()), ("Ceiling", "Confidential"), ("Reason", "Rehearsal approver"))), "Adding a member");
            Assert.Contains(groupKey, await GetAsync(orgAdmin, "/Admin/ApproverGroups"));
            return $"Group {groupKey} registered with one approver, after a preview of who could approve before and after.";
        });

        // 9-11. The three documents that used to be typed as JSON are filled in as forms.
        await ledger.Step(9, "Contract workflow: change a button label in the form, check, submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Forms/Edit?kind=WorkflowContractLifecycle", async () =>
        {
            var page = Words(await GetAsync(orgAdmin, $"/Admin/Forms/Edit?kind=WorkflowContractLifecycle&organisationId={org}"));
            Assert.Contains("Moves between stages", page);
            Assert.DoesNotContain("\"transitions\"", page);
            var fields = await FormPostAsync(app, "WorkflowContractLifecycle", org, d => { d["transitions"]![0]!["display"] = "Send onwards for review"; return d; });
            var checkedForm = await Send(orgAdmin, $"/Admin/Forms/Edit?kind=WorkflowContractLifecycle&organisationId={org}", "Validate", fields);
            Assert.True(checkedForm.Status is HttpStatusCode.Redirect or HttpStatusCode.OK, "workflow check");
            var submitted = await Send(orgAdmin, $"/Admin/Forms/Edit?kind=WorkflowContractLifecycle&organisationId={org}", "Publish", [.. fields, new("Note", "Clearer button")]);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Workflow submit: " + Words(submitted.Body)[..Math.Min(500, Words(submitted.Body).Length)]);
            return "Edited with fields and buttons; submitted for review.";
        });

        await ledger.Step(10, "Approval matrix: build one in the form (a matrix, a row, a step), check, submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Forms/Edit?kind=ApprovalMatrix", async () =>
        {
            ExpectDone(await Send(orgAdmin, $"/Admin/Forms/Edit?kind=ApprovalMatrix&organisationId={org}", "Start", Fields(("Kind", "ApprovalMatrix"), ("OrganisationId", org.ToString()))), "Starting the matrix draft");
            var fields = await FormPostAsync(app, "ApprovalMatrix", org, d =>
            {
                d["matrices"] = new JsonArray(new JsonObject
                {
                    ["code"] = "rehearsal-approvals", ["name"] = "Rehearsal approvals", ["currency"] = "AUD",
                    ["rows"] = new JsonArray(new JsonObject
                    {
                        ["code"] = "all-contracts", ["kind"] = "Base", ["priority"] = 100, ["flow"] = "Sequential", ["when"] = new JsonObject(),
                        ["steps"] = new JsonArray(new JsonObject { ["key"] = "approver", ["display"] = "Rehearsal approver", ["approverGroup"] = groupKey, ["mode"] = "FirstResponse" }),
                    }),
                });
                return d;
            });
            var checkedForm = await Send(orgAdmin, $"/Admin/Forms/Edit?kind=ApprovalMatrix&organisationId={org}", "Validate", fields);
            var text = Words(checkedForm.Body);
            Assert.DoesNotContain("Fix these before you submit", text);
            var submitted = await Send(orgAdmin, $"/Admin/Forms/Edit?kind=ApprovalMatrix&organisationId={org}", "Publish", fields);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Matrix submit: " + Words(submitted.Body)[..Math.Min(500, Words(submitted.Body).Length)]);
            return "A matrix with one row and one step built entirely from fields; submitted for review.";
        });

        await ledger.Step(11, "Notification policy: reword a message in the form, read the preview, submit", "Organisation Administrator", "OrganisationAdministrator", "/Admin/Forms/Edit?kind=NotificationPolicy", async () =>
        {
            var preview = Words(await GetAsync(orgAdmin, $"/Admin/Forms/Edit?kind=NotificationPolicy&organisationId={org}"));
            Assert.Contains("What would be sent", preview);
            var fields = await FormPostAsync(app, "NotificationPolicy", org, d => { var first = d["templates"]!.AsObject().First(); first.Value!["subject"] = "Rehearsal: {{contract.reference}} needs attention"; return d; });
            var submitted = await Send(orgAdmin, $"/Admin/Forms/Edit?kind=NotificationPolicy&organisationId={org}", "Publish", [.. fields, new("Note", "Rehearsal wording")]);
            Assert.True(submitted.Status is HttpStatusCode.Redirect, "Policy submit: " + Words(submitted.Body)[..Math.Min(500, Words(submitted.Body).Length)]);
            return "Message reworded in the form; sample shown before submitting; submitted for review.";
        });

        // 12. A second person reviews and publishes everything; nobody publishes their own work.
        await ledger.Step(12, "Review inbox: read each plain-language summary and publish; the author cannot", "Business Configuration Administrator", "BusinessConfigurationAdministrator", "/Admin/Reviews, /Admin/Reviews/Details", async () =>
        {
            var queue = await GetAsync(bizConfig, "/Admin/Reviews");
            var ids = Regex.Matches(queue, $"/Admin/Reviews/Details\\?id=({Pattern})").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(ids.Count >= 5, $"Expected the submissions in the inbox but found {ids.Count}.");
            var published = 0; var order = new[] { "Regional", "Terminology", "Capabilities", "Approval", "Contract workflow", "Notification" };
            var details = new List<(string Id, string Text)>();
            foreach (var id in ids) details.Add((id, Words(await GetAsync(bizConfig, $"/Admin/Reviews/Details?id={id}"))));
            foreach (var (id, text) in details.OrderBy(d => Array.FindIndex(order, o => d.Text.Contains(o, StringComparison.OrdinalIgnoreCase)) is var i && i < 0 ? 99 : i))
            {
                Assert.DoesNotContain("\"schemaVersion\"", text);
                if (!text.Contains("Approve and publish")) continue;                        // someone else's work only
                // The person who submitted it is refused when they try to publish it themselves.
                var own = await Send(orgAdmin, $"/Admin/Reviews/Details?id={id}", "Publish", Fields(("Id", id), ("Note", "mine")));
                Assert.Equal(HttpStatusCode.OK, own.Status);
                var done = await Send(bizConfig, $"/Admin/Reviews/Details?id={id}", "Publish", Fields(("Id", id), ("Note", "Reviewed in the rehearsal")));
                if (done.Status == HttpStatusCode.Redirect) published++;
                else throw new InvalidOperationException("Publishing was not accepted: " + Words(done.Body)[..Math.Min(500, Words(done.Body).Length)]);
            }
            Assert.True(published >= 5, $"Published {published}.");
            var checklist = Words(await GetAsync(orgAdmin, $"/Admin/Organisations/Setup?id={org}"));
            Assert.Matches("Regional profile\\s+Done", checklist);
            Assert.Matches("Contract workflow\\s+Done", checklist);
            return $"{published} submissions reviewed and published by a second person; authors were refused for their own work; the checklist shows them done.";
        });

        // 13-19. The counterparties, contracts and attachments of the old register are loaded by the procurement administrators.
        string projectUrl = "", batchUrl = "", profileUrl = ""; Guid versionId = Guid.Empty;
        await ledger.Step(13, "Migration: create the project and the mapping profile from the sample files", "Procurement Administrator", "ProcurementAdministrator", "/Migration, /Migration/Profiles", async () =>
        {
            var created = await Send(procurement, "/Migration", null, Fields(("OrganisationId", org.ToString()), ("Name", "Rehearsal register load"), ("SourceSystem", "Legacy register"), ("SourceSystemVersion", "2026.1"),
                ("DataOwner", "Data Owner"), ("ExtractionDate", DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd"))));
            ExpectDone(created, "Project");
            projectUrl = created.Location!;
            var profile = await Send(procurement, "/Migration/Profiles", null, Fields(("OrganisationId", org.ToString()), ("Name", "Rehearsal profile"), ("SourceSystem", "Legacy register"), ("SourceSystemVersion", "2026.1")));
            ExpectDone(profile, "Profile");
            profileUrl = profile.Location!;
            versionId = FirstGuid(await GetAsync(procurement, profileUrl), $"name=\"VersionId\" value=\"({Pattern})\"");
            ExpectDone(await UploadAsync(procurement, profileUrl, "Fields", "field-mapping-profile-complete.csv", MigrationSamples.FieldMappings, ("VersionId", versionId.ToString())), "field mappings");
            ExpectDone(await UploadAsync(procurement, profileUrl, "Lookups", "status-lookup-map.csv", MigrationSamples.StatusLookups, ("VersionId", versionId.ToString())), "lookups");
            ExpectDone(await Send(procurement, profileUrl, "Options", Fields(("VersionId", versionId.ToString()), ("OpenEndedTypes", "Evergreen"), ("CreateMissingCategories", "true"))), "options");
            ExpectDone(await Send(procurement, profileUrl, "Publish", Fields(("VersionId", versionId.ToString()), ("PublishNote", "Rehearsal profile"))), "profile publication");
            return "Project and an unchangeable mapping profile created from two spreadsheet files.";
        });

        await ledger.Step(14, "Migration: load a file with a mistake, read the error report, see that commit is refused", "Procurement Administrator", "ProcurementAdministrator", "/Migration/Batches, error report", async () =>
        {
            ExpectDone(await Send(procurement, projectUrl, "Legacy", Fields(("AllowedStatuses", "Effective, Ended"), ("AllowRegisteredDate", "true"), ("ExpiresDate", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd")), ("AllowOverwrite", "false"))), "policy");
            var batch = await Send(procurement, projectUrl, "CreateBatch", Fields(("BatchName", "First load (one mistake)"), ("Kind", "Final"), ("Strategy", "SkipExisting"), ("DocumentOption", "AddVersion"), ("IsLegacyLoad", "true"), ("ProfileVersionId", versionId.ToString())));
            ExpectDone(batch, "Batch");
            var bad = System.Text.Encoding.UTF8.GetString(MigrationSamples.ContractsCsv).Replace("2024-01-01,2024-01-01,2024-01-15,2026-11-30,2027-12-31,AUD,125000", "not-a-date,2024-01-01,2024-01-15,2026-11-30,2027-12-31,AUD,125000");
            ExpectDone(await UploadAsync(procurement, batch.Location!, "UploadContracts", "contracts-import.csv", System.Text.Encoding.UTF8.GetBytes(bad)), "contracts upload");
            ExpectDone(await UploadAsync(procurement, batch.Location!, "UploadArchive", "attachments.zip", MigrationSamples.AttachmentZip(includeManifest: true)), "archive upload");
            ExpectDone(await Send(procurement, batch.Location!, "Validate", []), "validation");
            var report = Words(await GetAsync(procurement, batch.Location!));
            Assert.Matches("(?i)error", report);
            Assert.Matches("(?i)not-a-date|not a valid date|date", report);
            var tooEarly = await Send(procurement, batch.Location!, "Commit", []);
            Assert.DoesNotContain("Committed", Words(tooEarly.Status == HttpStatusCode.Redirect ? await GetAsync(procurement, batch.Location!) : tooEarly.Body));
            var errors = await procurement.GetAsync($"{batch.Location}?handler=ErrorsXlsx");
            Assert.Equal(HttpStatusCode.OK, errors.StatusCode);
            return "The error report names the row and the problem; the batch cannot be committed; the error report downloads as a workbook.";
        });

        await ledger.Step(15, "Migration: fix the source file, load it again in a new batch, dry-run it clean", "Procurement Administrator", "ProcurementAdministrator", "/Migration/Batches", async () =>
        {
            var batch = await Send(procurement, projectUrl, "CreateBatch", Fields(("BatchName", "Second load (corrected)"), ("Kind", "Final"), ("Strategy", "SkipExisting"), ("DocumentOption", "AddVersion"), ("IsLegacyLoad", "true"), ("ProfileVersionId", versionId.ToString())));
            ExpectDone(batch, "Batch 2");
            batchUrl = batch.Location!;
            ExpectDone(await UploadAsync(procurement, batchUrl, "UploadContracts", "contracts-import.csv", MigrationSamples.ContractsCsv), "contracts upload");
            ExpectDone(await UploadAsync(procurement, batchUrl, "UploadArchive", "attachments.zip", MigrationSamples.AttachmentZip(includeManifest: true)), "archive upload");
            ExpectDone(await Send(procurement, batchUrl, "Validate", []), "validation");
            var page = Words(await GetAsync(procurement, batchUrl));
            Assert.Contains("would load", page);
            return "Dry run: nothing written; the page says what would load.";
        });

        await ledger.Step(16, "Data handling: say the file is masked company data; commit is refused until the declaration exists", "Procurement Administrator", "ProcurementAdministrator", "/Migration/MaskedData, /Migration/Batches", async () =>
        {
            // This installation requires every batch to be classified before it commits. Without a classification the commit is refused with a code and a plain sentence.
            var unclassified = await Send(procurement, batchUrl, "Commit", []);
            Assert.Contains("MDD-003", Words(unclassified.Status == HttpStatusCode.Redirect ? await GetAsync(procurement, batchUrl) : unclassified.Body));
            // The company masks its own data; the platform records what the company says about it.
            ExpectDone(await Send(procurement, $"/Migration/MaskedData?organisationId={org}", "Declare", Fields(("OrganisationId", org.ToString()), ("DatasetOwner", "Finance Systems"), ("SourceSystem", "Legacy register"),
                ("MaskingMethod", "Counterparty names and contract values replaced with generated values by the data owner before extraction"), ("FieldsMasked", "Counterparty name, Contact email, Contract value"),
                ("AttachmentsIncluded", "true"), ("ApprovalReference", "CHG-2026-0417"), ("ApprovedBy", "Head of Data Governance"), ("ApprovedOn", DateTime.UtcNow.ToString("yyyy-MM-dd")), ("ReviewBy", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd")),
                ("PermittedUsers", ProcAdmin + ", " + ProcAdmin2))), "declaration");
            var declaration = FirstGuid(await GetAsync(procurement, batchUrl), $"<option value=\"({Pattern})\">Finance Systems");
            ExpectDone(await Send(procurement, batchUrl, "Classify", Fields(("DataClass", "MaskedCompanyData"), ("DeclarationId", declaration.ToString()))), "classification");
            Assert.Contains("Masked Company Data", Words(await GetAsync(procurement, batchUrl)));
            // With a current declaration the next gate is the scanner: the only one connected is the labelled demonstration scanner, which nobody has accepted for this rehearsal.
            var behindMock = await Send(procurement, batchUrl, "Commit", []);
            Assert.Contains("MDD-006", Words(behindMock.Status == HttpStatusCode.Redirect ? await GetAsync(procurement, batchUrl) : behindMock.Body));
            return "Commit was refused three ways in turn (no classification, then, with the declaration made and linked, the unaccepted demonstration scanner); each gave a code and a sentence. The declaration records owner, source, method, fields, approval and expiry, and holds no data.";
        });

        await ledger.Step(17, "Operations: accept the demonstration scanner for this masked-data rehearsal, on the record", "System Administrator", "SystemAdministrator (technical)", "/Admin/Operations/Scanner", async () =>
        {
            var page = Words(await GetAsync(sysAdmin, "/Admin/Operations/Scanner"));
            Assert.Contains("TEST MODE", page);
            Assert.Contains("Not Verified", page);            // the demonstration scanner never counts as a verified scanner
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/Admin/Operations/Scanner")).StatusCode);
            ExpectDone(await Send(sysAdmin, "/Admin/Operations/Scanner", "Accept", Fields(("Scope", "masked-data rehearsal, Test environment"), ("Reason", "Rehearsal of the migration with masked data; no real protection is claimed."), ("ValidDays", "14"))), "acceptance");
            var after = Words(await GetAsync(sysAdmin, "/Admin/Operations/Scanner"));
            Assert.Contains("Accepted", after);
            Assert.Contains("masked-data rehearsal", after);
            return "The demonstration scanner was accepted for 14 days with a reason and a scope; the page still labels it TEST MODE and Not Verified.";
        }, technical: true);

        await ledger.Step(18, "Migration: a second person confirms the legacy load, then it is committed", "Procurement Administrator (second person)", "ProcurementAdministrator", "/Migration/Batches", async () =>
        {
            var tooEarly = await Send(procurement, batchUrl, "Commit", []);
            Assert.Contains("second person", Words(tooEarly.Status == HttpStatusCode.Redirect ? await GetAsync(procurement, batchUrl) : tooEarly.Body));
            ExpectDone(await Send(procurementSecond, batchUrl, "Confirm", []), "confirmation");
            ExpectDone(await Send(procurement, batchUrl, "Commit", []), "commit");
            var afterCommit = Words(await GetAsync(procurement, batchUrl));
            Assert.True(afterCommit.Contains("Committed"), afterCommit);
            return "Committed after a different person confirmed; the committer alone was refused.";
        });

        await ledger.Step(19, "Migration: reconcile the counts and record acceptance", "Procurement Administrator", "ProcurementAdministrator", "/Migration/Batches/Reconciliation", async () =>
        {
            ExpectDone(await Send(procurement, batchUrl, "Reconcile", []), "reconciliation");
            Assert.Contains("Balanced", Words(await GetAsync(procurement, $"{batchUrl}/Reconciliation")));
            ExpectDone(await Send(procurement, batchUrl, "Accept", Fields(("Note", "Checked both contracts and three documents against the source."), ("Acknowledge", "true"))), "acceptance");
            Assert.Contains("Accepted", Words(await GetAsync(procurement, batchUrl)));
            return "Counts balanced; acceptance recorded with a note.";
        });

        // 20. User acceptance: the loaded register is read by the people who will use it.
        await ledger.Step(20, "UAT: find the loaded contracts, open one, see its documents and where it came from", "Procurement Administrator (second person)", "ProcurementAdministrator", "/Contracts, /Contracts/Details, /Suppliers", async () =>
        {
            var register = await GetAsync(procurementSecond, $"/Contracts?view=system:all-contracts&q=DEMO-AGR&org={org}");
            var (tenantId, _) = await app.FindUserAsync(ProcAdmin);
            var held = await app.WithTenantAsync(tenantId, async sp => string.Join(",", await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(sp.GetRequiredService<Ecp.Contracts.Persistence.ContractsDbContext>().Contracts).Where(c => c.AgreementReference.StartsWith("DEMO")).Select(c => c.AgreementReference + ":" + c.OrganisationId + ":" + c.Status))));
            Assert.True(register.Contains("DEMO-AGR-0001") && register.Contains("DEMO-AGR-0002"), "New org " + org + ". In the database: [" + held + "]. Page: " + Words(register)[^400..]);
            var id = FirstGuid(Regex.Match(register, $"/Contracts/Details/({Pattern})[^>]*>[^<]*DEMO-AGR-0001").Value + " ", $"/Contracts/Details/({Pattern})");
            var details = await GetAsync(procurementSecond, $"/Contracts/Details/{id}");
            Assert.Contains("Migration provenance", details);
            Assert.Contains("DEMO-AGR-0001_executed.pdf", details);
            var link = Regex.Match(details, $"href=\"(/Documents/Download/{Pattern})\"").Groups[1].Value;
            var download = await procurementSecond.GetAsync(link);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Contains("Southern Cross Demo Supplies", await GetAsync(procurementSecond, "/Suppliers"));
            return "Both contracts, the counterparties and the attachments are visible; a document downloads; provenance is shown.";
        });

        // 21. The technical steps are named as technical: a System Administrator, in the installation, never a business administrator.
        // 21. The System Administrator opens the dashboard and takes a verified backup: the way back that the upgrade pre-flight needs. Backups need SQL Server and the operator's shared backup folder.
        string? backupId = null;
        await ledger.JudgedStep(21, "Operations: open the dashboard and take a verified backup", "System Administrator", "SystemAdministrator (technical)", "/Admin/Operations, /Admin/Operations/Backups", async () =>
        {
            var dashboard = Words(await GetAsync(sysAdmin, "/Admin/Operations"));
            Assert.Contains("Operations", dashboard);
            Assert.Contains("TestEnforced", Words(await GetAsync(sysAdmin, "/Admin/Operations/Dashboard")));
            // A business administrator reaches none of it.
            Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync("/Admin/Operations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/Admin/Operations/Backups")).StatusCode);
            var page = await GetAsync(sysAdmin, "/Admin/Operations/Backups");
            if (!app.IsSqlServer || !page.Contains("name=\"Reason\""))
                return ("The backup page was opened and says plainly that this database or configuration cannot take a backup here. Nothing was backed up.", EvidenceSemantics.Judge("Not Verified"));
            var made = await PostAsync(sysAdmin, "/Admin/Operations/Backups", "Start", Fields(("Reason", "No-code rehearsal: the way back before the upgrade check"), ("HoldMaintenance", "false")));
            Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
            var details = await GetAsync(sysAdmin, made.Headers.Location!.OriginalString);
            backupId = Regex.Match(details, "bk-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}").Value;
            Assert.NotEmpty(backupId);
            var state = Regex.Match(Words(details), "State (Verified with warnings|Verified|Unverified|Incomplete|Failed)").Groups[1].Value;
            return ($"Backup {backupId} was taken and read back; its state is '{state}'.", EvidenceSemantics.Judge(state.Length == 0 ? "Not Verified" : state, warningsAccepted: false));
        }, technical: true);

        // 22. The upgrade pre-flight against that backup. Its outcome is kept exactly as reported: Ready is Passed; Ready with warnings is not a pass until each warning is explicitly accepted; Blocked is Failed; Not Verified stays Not Verified.
        await ledger.JudgedStep(22, "Operations: run the upgrade pre-flight against the verified backup", "System Administrator", "SystemAdministrator (technical)", "/Admin/Operations/UpgradeReadiness", async () =>
        {
            var readiness = await GetAsync(sysAdmin, "/Admin/Operations/UpgradeReadiness");
            Assert.Contains("is not an approval to upgrade or to go live", readiness);
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/Admin/Operations/UpgradeReadiness")).StatusCode);
            // The pre-flight reads SQL Server (UPF-003). On the lightweight provider it cannot be run, so the step is recorded as Not Verified: never as a pass and never as a failure of the platform.
            if (!app.IsSqlServer)
                return ("The pre-flight page opened and says it is not an approval. It was not run: the pre-flight needs SQL Server.", EvidenceSemantics.Judge(PreflightOutcome.NotVerified));
            var json = await sysAdmin.GetAsync("/Admin/Operations/UpgradeReadiness?handler=Json" + (backupId is null ? "" : "&backupId=" + backupId));
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            var doc = System.Text.Json.JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement;
            var text = doc.GetProperty("outcome").GetString() ?? "";
            var outcome = Enum.TryParse<PreflightOutcome>(text.Replace(" ", ""), true, out var parsed) ? parsed : PreflightOutcome.NotVerified;
            List<string> CodesOf(string name) => doc.TryGetProperty("checks", out var checks) ? checks.EnumerateArray().Where(c => string.Equals(c.GetProperty("outcome").GetString(), name, StringComparison.OrdinalIgnoreCase)).Select(c => c.GetProperty("code").GetString() ?? "").ToList() : [];
            var warnings = CodesOf("Warning");
            var blockers = CodesOf("Failed");
            var evidence = await sysAdmin.GetAsync("/Admin/Operations/Evidence?handler=Json");
            Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);

            // Ready with warnings is a pass only if each warning is explicitly accepted by a named person, with a reason, on the record. The System Administrator does that on the pre-flight page.
            var before = EvidenceSemantics.Judge(outcome, warnings, acceptedCodes: []);
            if (outcome == PreflightOutcome.ReadyWithWarnings)
            {
                Assert.Equal(EvidenceVerdict.Warning, before.Verdict);   // not a pass before anyone accepts anything
                foreach (var code in warnings)
                {
                    var accepted = await PostAsync(sysAdmin, "/Admin/Operations/UpgradeReadiness", "Accept", Fields(("Code", code), ("Reason", $"Rehearsal: {code} was reviewed by the System Administrator and is acceptable for a supervised non-production rehearsal."), ("ValidDays", "30")));
                    Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
                }
            }
            // The page itself states what the result counts as, so the person never has to work it out.
            var shown = Words(await (await PostAsync(sysAdmin, "/Admin/Operations/UpgradeReadiness", "Run", Fields(("BackupId", backupId ?? "")))).Content.ReadAsStringAsync());
            var judgement = EvidenceSemantics.Judge(outcome, warnings, acceptedCodes: warnings);   // each warning was accepted above
            Assert.Contains("Counts as evidence " + EvidenceSemantics.Label(judgement.Verdict), shown);
            return ($"Pre-flight outcome '{judgement.Operational}' ({blockers.Count} blocker(s){(blockers.Count > 0 ? ": " + string.Join(", ", blockers) : "")}; {warnings.Count} warning(s){(warnings.Count > 0 ? ": " + string.Join(", ", warnings) + ", each explicitly accepted with a recorded reason" : "")}); counted as {EvidenceSemantics.Label(judgement.Verdict)} ({judgement.Code}). The evidence report downloaded.", judgement);
        }, technical: true);

        // 23. Email: the System Administrator connects the company's mail relay, and the platform proves it by delivering a synthetic message.
        await ledger.JudgedStep(23, "Operations: set up email, send the test message, read the readiness", "System Administrator", "SystemAdministrator (technical)", "/Admin/Operations/Email", async () =>
        {
            var before = Words(await GetAsync(sysAdmin, "/Admin/Operations/Email"));
            Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync("/Admin/Operations/Email")).StatusCode);
            ExpectDone(await Send(sysAdmin, "/Admin/Operations/Email", "Save", Fields(("Provider", "smtp"), ("From", "platform@rehearsal.example.test"), ("Host", "127.0.0.1"), ("Port", smtp.Port.ToString()), ("UseTls", "false"))), "email settings");
            Assert.Contains("NotVerified", await GetAsync(sysAdmin, "/Admin/Operations/Email"));   // saved but not proved
            ExpectDone(await Send(sysAdmin, "/Admin/Operations/Email", "Test", Fields(("TestRecipient", "ops.mailbox@rehearsal.example.test"))), "test message");
            var page = await GetAsync(sysAdmin, "/Admin/Operations/Email");
            var mail = Assert.Single(smtp.Mail);
            Assert.Contains("contains no contract information", mail.Data);
            var state = Regex.Match(page, "Readiness: <span class=\"badge [a-z]+\">([A-Za-z]+)</span>").Groups[1].Value;
            var judgement = state == "Healthy" ? EvidenceSemantics.Judge(CheckOutcome.Passed) : state == "Failed" ? EvidenceSemantics.Judge(CheckOutcome.Failed) : EvidenceSemantics.Judge(CheckOutcome.NotVerified);
            return ($"Email readiness '{state}': the loopback mail relay received one synthetic test message with no contract information; the page shows its reference, masked recipient and when a pass expires.", judgement);
        }, technical: true);

        return ledger;
    }
}

/// <summary>
/// What the ledger must show for the rehearsal to count: no step Failed, none left as an unaccepted Warning, every business step used a page, the technical steps are exactly the ones named, Not Verified appears only where
/// it is honest (a technical step the environment cannot check) and is reported as Not Verified, and the evidence is written out. A Blocked operation is never counted as a pass (G-37).
/// </summary>
internal static class RehearsalVerdict
{
    public static void Check(RehearsalLedger ledger, DemoApp app)
    {
        Assert.Equal(23, ledger.Steps.Count);
        Assert.All(ledger.Steps, s => Assert.True(s.Verdict is EvidenceVerdict.Passed or EvidenceVerdict.NotVerified, s.Number + " " + s.Title + ": " + s.Operational + " counted as " + s.Verdict + ". " + s.Outcome));
        Assert.Equal(0, ledger.Count(EvidenceVerdict.Failed));
        Assert.Equal(0, ledger.Count(EvidenceVerdict.Warning));
        // The recorded verdict is exactly what the semantics give for the recorded operational outcome: nothing was converted.
        Assert.All(ledger.Steps, s => Assert.Equal(EvidenceSemantics.Judge(s.Operational, warningsAccepted: s.Operational == "Ready with warnings" && s.Outcome.Contains("explicitly accepted")).Verdict, s.Verdict));
        Assert.All(ledger.Steps.Where(s => s.Verdict == EvidenceVerdict.NotVerified), s => Assert.True(s.TechnicalRole, $"Step {s.Number} is Not Verified but is not a technical step."));
        var business = ledger.Steps.Where(s => !s.TechnicalRole).ToList();
        var technical = ledger.Steps.Where(s => s.TechnicalRole).ToList();
        // Role separation: the technical steps are the System Administrator's; no business step names a command line, SQL, Git or a document.
        Assert.Equal([17, 21, 22, 23], technical.Select(s => s.Number));
        Assert.All(technical, s => Assert.Contains("SystemAdministrator", s.Role));
        Assert.All(business, s => { Assert.StartsWith("/", s.Via); Assert.DoesNotMatch("(?i)\\b(sql|git|json|cli|ecp-pack|dotnet|powershell|bash)\\b", s.Via + " " + s.Title); Assert.DoesNotContain("SystemAdministrator", s.Role); });
        Assert.Contains(business, s => s.Role == "TenantAdministrator");
        Assert.Contains(business, s => s.Role == "OrganisationAdministrator");
        Assert.Contains(business, s => s.Role == "BusinessConfigurationAdministrator");
        Assert.Contains(business, s => s.Role == "ProcurementAdministrator");
        if (!app.IsSqlServer) Assert.Equal(EvidenceVerdict.NotVerified, ledger.Steps[20].Verdict);   // step 21: the lightweight provider cannot take a backup, and the ledger says so
        var provider = app.IsSqlServer ? "SQL Server" : "lightweight provider (SQLite)";
        var markdown = ledger.Markdown(provider, app.IsSqlServer ? "real SQL Server: evidence for constraints, row-level security and concurrency" : "NOT SQL Server evidence");
        var folder = Path.Combine(AppContext.BaseDirectory, "rehearsal-evidence");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, app.IsSqlServer ? "no-code-rehearsal-sqlserver.md" : "no-code-rehearsal-sqlite.md"), markdown);
        Assert.Contains("| 23 |", markdown);
        Assert.DoesNotContain("| Blocked | Passed |", markdown);
    }
}

[Collection("Api")]
[Trait("Category", "Rehearsal")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class NoCodeRehearsalSqliteTests
{
    [Fact]
    public async Task A_new_company_is_set_up_from_nothing_through_the_pages_by_the_people_who_would_do_it()
    {
        using var app = DemoApp.Start(NoCodeJourney.Settings);
        var ledger = await NoCodeJourney.RunAsync(app);
        RehearsalVerdict.Check(ledger, app);
    }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class NoCodeRehearsalSqlServerTests
{
    [SqlServerFact]
    public async Task The_same_journey_over_real_SQL_Server_with_real_constraints_and_row_level_security()
    {
        using var app = DemoApp.StartSqlServer(NoCodeJourney.Settings);
        var ledger = await NoCodeJourney.RunAsync(app);
        RehearsalVerdict.Check(ledger, app);
    }
}
