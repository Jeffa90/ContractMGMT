using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record QaClearInput(string? Confirm);

/// <summary>
/// QA only (D-111, Jeff, 26 Nov): clear every contract, supplier and contract list before a rehearsal import, so numbers and names
/// don't cross over. Admins only, and only where demo data is allowed (QA and local; never Production). Kept: people and their
/// ticks, settings, email wording, reminder settings, contract fields and sections, currencies, supplier tiers, risk levels,
/// supplier document types, document kinds, feedback and the audit trail (it records the clear). The contract numbering starts
/// again. Files of the removed documents are deleted from the data folder once the clear is saved.
/// </summary>
public static class QaClearEndpoints
{
    /// <summary>The contract lists cleared (currencies and the supplier lists stay).</summary>
    private static readonly string[] ClearedLists = ["AgreementType", "BusinessUnit", "Site", "Entity", "Function"];

    public static void MapQaClearEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/qa-clear", async (QaClearInput body, CurrentUser current, Db db, AuditLog audit, AppSettings settings, ILoggerFactory logs) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can clear QA.");
            if (!settings.AllowDemoData) return ApiResults.NotAllowed("This can't be done here. It's only for QA.");
            if (!string.Equals((body.Confirm ?? "").Trim(), "CLEAR", StringComparison.Ordinal))
                return ApiResults.Invalid("Type CLEAR to confirm.", new() { ["confirm"] = "Type CLEAR, in capitals, to confirm." });

            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var files = (await c.QueryAsync<string>("""
                SELECT StoredName FROM contracts.DocumentVersion UNION ALL SELECT StoredName FROM contracts.SupplierDocumentVersion
                """, transaction: tx)).ToList();
            var counts = new
            {
                contracts = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Contract", transaction: tx),
                suppliers = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Supplier", transaction: tx),
                categories = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Category", transaction: tx),
                listItems = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName IN @ClearedLists", new { ClearedLists }, tx),
                files = files.Count,
            };
            // The append-only Legal conversation and approval decisions allow a delete only in a session marked like this (26_qa_clear.sql).
            await c.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'jbs.qa-clear', @value = 1", transaction: tx);
            try
            {
                await c.ExecuteAsync("""
                    DELETE FROM contracts.ReminderSummaryItem WHERE ContractId IS NOT NULL OR SupplierId IS NOT NULL;
                    DELETE FROM contracts.EmailOutbox WHERE ContractId IS NOT NULL;
                    DELETE FROM contracts.Reminder;
                    DELETE FROM contracts.Unlock;
                    DELETE FROM contracts.ContractFieldValue;
                    DELETE FROM contracts.ContractListValue;
                    DELETE FROM contracts.LegalTurnDocument;
                    DELETE FROM contracts.LegalTurn;
                    DELETE FROM contracts.Approval;
                    DELETE FROM contracts.DocumentVersion;
                    DELETE FROM contracts.Document;
                    DELETE FROM contracts.Variation;
                    UPDATE contracts.Contract SET RenewsContractId = NULL WHERE RenewsContractId IS NOT NULL;
                    DELETE FROM contracts.Contract;

                    DELETE FROM contracts.SupplierDocumentReminder;
                    DELETE FROM contracts.SupplierDocumentWatch;
                    DELETE FROM contracts.SupplierDocumentVersion;
                    DELETE FROM contracts.SupplierDocument;
                    DELETE FROM contracts.BusinessReviewAction;
                    DELETE FROM contracts.BusinessReviewAttendee;
                    DELETE FROM contracts.BusinessReviewContact;
                    DELETE FROM contracts.BusinessReview;
                    DELETE FROM contracts.SupplierContact;
                    DELETE FROM contracts.SupplierReviewReminder;
                    DELETE FROM contracts.Supplier;

                    DELETE FROM contracts.Category WHERE ParentId IS NOT NULL;
                    DELETE FROM contracts.Category;
                    DELETE FROM contracts.ListItem WHERE ListName IN @ClearedLists;
                    UPDATE contracts.Counter SET NextValue = 1 WHERE Name = N'ContractNumber';
                    """, new { ClearedLists }, tx, commandTimeout: 300);
            }
            finally
            {
                await c.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'jbs.qa-clear', @value = NULL", transaction: tx);
            }
            await audit.WriteAsync(c, tx, me.AsActor(), "qa.cleared", "System", null,
                $"{me.Name} cleared QA for a fresh start: {counts.contracts} contracts, {counts.suppliers} suppliers, {counts.categories} categories and subcategories, " +
                $"{counts.listItems} other list values and {counts.files} files removed. People, settings and the audit trail were kept; contract numbers start again.",
                counts);
            await tx.CommitAsync();

            // The files go once the clear is saved. A file that can't be removed is only logged (it's no longer linked to anything).
            if (settings.DocumentsDirectory is { } root)
            {
                var full = Path.GetFullPath(root);
                var log = logs.CreateLogger("QaClear");
                foreach (var stored in files)
                {
                    var path = Path.GetFullPath(Path.Combine(full, stored.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException ex) { log.LogWarning(ex, "Couldn't remove a cleared file."); }
                    catch (UnauthorizedAccessException ex) { log.LogWarning(ex, "Couldn't remove a cleared file."); }
                }
            }
            return Results.Ok(counts);
        });
    }
}
