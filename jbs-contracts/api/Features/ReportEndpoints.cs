using System.Globalization;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Reports for admins and auditors (product-spec sections 11 and 13; D-82 to D-84): the register, the pipeline, approvals and the audit
/// trail, each on screen and as an Excel download. The register also prints cleanly to PDF from the browser (D-82). Auditors only read.
/// </summary>
public static class ReportEndpoints
{
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brisbane);

    private static async Task<(Person? Me, IResult? Refusal)> RequireAsync(CurrentUser current, Db db)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return (null, refusal);
        return me!.IsAdmin || me.IsAuditor ? (me, null) : (null, ApiResults.NotAllowed("Reports are for admins and auditors."));
    }

    private static IResult File(byte[] bytes, string name, TimeProvider clock) =>
        Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"jbs-contracts-{name}-{TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brisbane):yyyy-MM-dd}.xlsx");

    // ---------------------------------------------------------------- Register

    private static string StagesFor(string? scope) => scope switch
    {
        "ended" => "N'Effective', N'Ended'",
        "all" => "N'Effective', N'Ended', N'Archived'",
        _ => "N'Effective'",
    };

    private sealed class RegisterRow
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? SupplierName { get; set; }
        public string? CategoryName { get; set; }
        public string? ParentCategoryName { get; set; }
        public string? AgreementTypeName { get; set; }
        public string? BusinessUnitName { get; set; }
        public string? SiteName { get; set; }
        public string OwnerName { get; set; } = "";
        public string? BackupOwnerName { get; set; }
        public bool IsConfidential { get; set; }
        public decimal? Value { get; set; }
        public string CurrencyCode { get; set; } = "AUD";
        public string? SapOutlineAgreement { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
        public int? NoticePeriodDays { get; set; }
        public DateTime? ReviewDate { get; set; }
        public DateTime? RegisteredUtc { get; set; }
        public string? RegisteredByName { get; set; }
        public string? RenewalDecision { get; set; }
        public DateTime? EndedOn { get; set; }

        public DateTime? NoticeDeadline => NoticePeriodDays is not { } n ? null : (IsEvergreen ? ReviewDate : EndDate) is { } a ? a.AddDays(-n) : null;
        public string? Category => CategoryName is null ? null : ParentCategoryName is null ? CategoryName : $"{ParentCategoryName} > {CategoryName}";
    }

    private static async Task<(List<RegisterRow> Rows, List<Fields.ExcelField> Added, Dictionary<int, Dictionary<string, (object? Value, string Display)>> Values)> RegisterAsync(
        SqlConnection c, Person me, string? scope)
    {
        var rows = (await c.QueryAsync<RegisterRow>($"""
            SELECT k.Id, k.Reference, k.Title, k.Stage, s.Name AS SupplierName, cat.Name AS CategoryName, parent.Name AS ParentCategoryName, at.Value AS AgreementTypeName,
                   bu.Value AS BusinessUnitName, site.Value AS SiteName, o.Name AS OwnerName, b.Name AS BackupOwnerName, k.IsConfidential, k.Value, k.CurrencyCode,
                   k.SapOutlineAgreement, k.StartDate, k.EffectiveDate, k.EndDate, k.IsEvergreen, k.NoticePeriodDays, k.ReviewDate, k.RegisteredUtc, rb.Name AS RegisteredByName,
                   k.RenewalDecision, k.EndedOn
            FROM contracts.Contract k
            LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
            LEFT JOIN contracts.Category cat ON cat.Id = k.CategoryId LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
            LEFT JOIN contracts.ListItem at ON at.Id = k.AgreementTypeId LEFT JOIN contracts.ListItem bu ON bu.Id = k.BusinessUnitId LEFT JOIN contracts.ListItem site ON site.Id = k.SiteId
            JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Person b ON b.Id = k.BackupOwnerId LEFT JOIN contracts.Person rb ON rb.Id = k.RegisteredById
            WHERE k.Stage IN ({StagesFor(scope)}) AND {ContractAccess.VisibleSql("k")}
            ORDER BY k.Reference
            """, ContractAccess.Params(me))).ToList();
        var defs = await Fields.LoadAsync(c);
        var added = Fields.ExcelFields(defs, Headers);
        var values = await Fields.ReadAsync(c, null, rows.Select(r => r.Id), added.Select(a => a.Field).ToList());
        return (rows, added, values);
    }

    private static readonly Excel.Column[] RegisterColumns =
    [
        new("Reference", null, 16), new("Title", null, 34), new("Stage", null, 11), new("Supplier", null, 28), new("Category", null, 24), new("Agreement type", null, 18),
        new("Business unit", null, 16), new("Site", null, 16), new("Owner", null, 20), new("Backup owner", null, 20), new("Confidential", null, 12),
        new("Value", null, 16), new("Currency", null, 10), new("SAP outline agreement number", null, 18), new("Start date", null, 13), new("Effective date", null, 13),
        new("End date", null, 13), new("Evergreen", null, 11), new("Notice period (days)", null, 12), new("Notice deadline", null, 13), new("Review date", null, 13),
        new("Registered", null, 13), new("Registered by", null, 20), new("Renewal decision", null, 15), new("Ended on", null, 13),
    ];
    private static readonly string[] Headers = RegisterColumns.Select(c => c.Header).ToArray();

    // ---------------------------------------------------------------- Endpoints

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("/register", async (string? scope, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var (rows, added, values) = await RegisterAsync(c, me!, scope);
            return Results.Ok(new
            {
                columns = added.Select(a => new { a.Field.Key, Label = a.Header }),
                rows = rows.Select(r => new
                {
                    r.Id, r.Reference, r.Title, r.Stage, StageName = ContractEndpoints.StageNames[r.Stage], r.SupplierName, r.Category, r.OwnerName, r.IsConfidential,
                    r.Value, r.CurrencyCode, EffectiveDate = D(r.EffectiveDate), EndDate = D(r.EndDate), r.IsEvergreen, NoticeDeadline = D(r.NoticeDeadline),
                    RegisteredUtc = ContractEndpoints.Utc(r.RegisteredUtc), r.RegisteredByName, r.BusinessUnitName, r.SiteName,
                    RenewalDecision = r.RenewalDecision is { } d ? RenewalEndpoints.DecisionNames[d] : null,
                    Fields = values[r.Id].ToDictionary(v => v.Key, v => v.Value.Display),
                }),
                totals = new
                {
                    count = rows.Count,
                    value = rows.Where(r => r.Value is not null).GroupBy(r => r.CurrencyCode).Select(g => new { currency = g.Key, total = g.Sum(r => r.Value!.Value) }),
                },
            });
        });

        group.MapGet("/register.xlsx", async (string? scope, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var (rows, added, values) = await RegisterAsync(c, me!, scope);
            var people = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person")).ToDictionary(p => p.Id);
            var columns = RegisterColumns.Concat(added.Select(a => new Excel.Column(a.Header, null, a.Field.Kind == "LongText" ? 40 : 20))).ToList();
            var data = rows.Select(r => new object?[]
            {
                r.Reference, r.Title, ContractEndpoints.StageNames[r.Stage], r.SupplierName, r.Category, r.AgreementTypeName, r.BusinessUnitName, r.SiteName,
                r.OwnerName, r.BackupOwnerName, r.IsConfidential, r.Value, r.CurrencyCode, r.SapOutlineAgreement, D(r.StartDate), D(r.EffectiveDate), D(r.EndDate),
                r.IsEvergreen, r.NoticePeriodDays, D(r.NoticeDeadline), r.RegisteredUtc is { } ru ? DateOnly.FromDateTime(Local(ru)) : null, r.RegisteredByName,
                r.RenewalDecision is { } dn ? RenewalEndpoints.DecisionNames[dn] : null, D(r.EndedOn),
            }.Concat(added.Select(a => values[r.Id].TryGetValue(a.Field.Key, out var v) ? Fields.ExcelValue(a.Field, v, id => people.GetValueOrDefault(id)?.WindowsAccount) : null)).ToArray());
            return File(Excel.Write("Register", columns, data), "register", clock);
        });

        group.MapGet("/pipeline", async (CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var now = clock.GetUtcNow().UtcDateTime;
            var open = (await c.QueryAsync<(int Id, string Reference, string Title, string Stage, DateTime StageSinceUtc, string? WhoseTurn, string? LegalPersonName, string OwnerName, string? SupplierName)>($"""
                SELECT k.Id, k.Reference, k.Title, k.Stage, k.StageSinceUtc, k.WhoseTurn, lp.Name AS LegalPersonName, o.Name AS OwnerName, s.Name AS SupplierName
                FROM contracts.Contract k JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Person lp ON lp.Id = k.LegalPersonId
                LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
                WHERE k.Stage IN (N'Draft', N'LegalReview', N'Signing', N'Approval') AND {ContractAccess.VisibleSql("k")}
                """, ContractAccess.Params(me!))).ToList();
            double Days(DateTime since) => Math.Max(0, (now - since).TotalDays);
            string[] order = ["Draft", "LegalReview", "Signing", "Approval"];
            var counts = await c.QueryAsync<(string Stage, int Count)>($"SELECT k.Stage, COUNT(*) FROM contracts.Contract k WHERE {ContractAccess.VisibleSql("k")} GROUP BY k.Stage", ContractAccess.Params(me!));
            var variations = await c.QueryAsync<(string Stage, int Count)>("SELECT Stage, COUNT(*) FROM contracts.Variation WHERE Stage IN (N'Draft', N'LegalReview', N'Approval') GROUP BY Stage");
            return Results.Ok(new
            {
                stages = order.Select(st =>
                {
                    var inStage = open.Where(o => o.Stage == st).ToList();
                    return new
                    {
                        Stage = st, Name = ContractEndpoints.StageNames[st], Count = inStage.Count,
                        AverageDays = inStage.Count == 0 ? 0 : Math.Round(inStage.Average(o => Days(o.StageSinceUtc)), 1),
                        LongestDays = inStage.Count == 0 ? 0 : (int)inStage.Max(o => Days(o.StageSinceUtc)),
                    };
                }),
                legal = new
                {
                    legalsTurn = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Legal"),
                    ownersTurn = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Owner"),
                    notPickedUp = open.Count(o => o.Stage == "LegalReview" && o.WhoseTurn == "Legal" && o.LegalPersonName is null),
                },
                totals = counts.ToDictionary(x => x.Stage, x => x.Count),
                variations = variations.ToDictionary(x => x.Stage, x => x.Count),
                waitingLongest = open.OrderByDescending(o => Days(o.StageSinceUtc)).Take(15).Select(o => new
                {
                    o.Id, o.Reference, o.Title, o.Stage, StageName = ContractEndpoints.StageNames[o.Stage], Days = (int)Days(o.StageSinceUtc),
                    With = o.Stage == "LegalReview" ? (o.WhoseTurn == "Legal" ? o.LegalPersonName ?? "Legal (not picked up)" : o.OwnerName) : o.Stage == "Approval" ? "Approvers" : o.OwnerName,
                    o.SupplierName,
                }),
            });
        });

        group.MapGet("/approvals", async (int? days, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await ApprovalsAsync(c, me!, days, clock);
            var decided = rows.Where(r => r.TurnaroundDays is not null).ToList();
            return Results.Ok(new
            {
                days = Window(days),
                rows,
                summary = new
                {
                    approved = rows.Count(r => r.Decision == "Approved"), sentBack = rows.Count(r => r.Decision == "SentBack"),
                    averageDays = decided.Count == 0 ? (double?)null : Math.Round(decided.Average(r => r.TurnaroundDays!.Value), 1),
                    byApprover = rows.GroupBy(r => r.ByName).Select(g => new
                    {
                        Name = g.Key, Approved = g.Count(r => r.Decision == "Approved"), SentBack = g.Count(r => r.Decision == "SentBack"),
                        AverageDays = g.Any(r => r.TurnaroundDays is not null) ? Math.Round(g.Where(r => r.TurnaroundDays is not null).Average(r => r.TurnaroundDays!.Value), 1) : (double?)null,
                    }).OrderBy(x => x.Name),
                },
            });
        });

        group.MapGet("/approvals.xlsx", async (int? days, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await ApprovalsAsync(c, me!, days, clock);
            Excel.Column[] columns = [new("Decided", null, 18), new("Reference", null, 16), new("Contract", null, 34), new("What", null, 14), new("Decision", null, 12),
                new("By", null, 22), new("As backup for", null, 22), new("Days waiting", null, 12), new("Reason", null, 40)];
            return File(Excel.Write("Approvals", columns, rows.Select(r => new object?[]
            {
                Local(r.AtUtc), r.Reference, r.Title, r.Variation is { } v ? $"Variation {v}" : "Contract", r.Decision == "Approved" ? "Approved" : "Sent back",
                r.ByName, r.AsBackupForName, r.TurnaroundDays is { } t ? (decimal)t : null, r.Reason,
            })), "approvals", clock);
        });

        group.MapGet("/audit", async (int? days, string? q, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await AuditAsync(c, days, q, clock, 500);
            var broken = await AuditLog.VerifyAsync(c);
            return Results.Ok(new
            {
                days = Window(days), intact = broken is null, brokenAt = broken,
                total = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent"),
                rows = rows.Select(r => new { r.Id, OccurredUtc = DateTime.SpecifyKind(r.OccurredUtc, DateTimeKind.Utc), r.ActorName, r.Action, r.Summary, r.ContractId, r.Reference }),
            });
        });

        group.MapGet("/audit.xlsx", async (int? days, string? q, CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
        {
            var (me, refusal) = await RequireAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var rows = await AuditAsync(c, days, q, clock, 100_000);
            Excel.Column[] columns = [new("Event", null, 10), new("When", null, 18), new("Who", null, 26), new("Action", null, 24), new("Contract", null, 16),
                new("What happened", null, 70), new("Details", "Old and new values, as recorded", 60), new("Hash", "Each event's hash includes the previous one, so a changed or removed event shows", 66)];
            // Exporting the audit is itself recorded.
            await using (var tx = await Db.BeginWriteAsync(c))
            {
                await audit.WriteAsync(c, tx, me!.AsActor(), "audit.exported", "Audit", null, $"{me.Name} exported {rows.Count:N0} audit events (last {Window(days)} days{(string.IsNullOrWhiteSpace(q) ? "" : $", matching \"{q.Trim()}\"")}).");
                await tx.CommitAsync();
            }
            return File(Excel.Write("Audit", columns, rows.Select(r => new object?[]
            {
                (int)r.Id, Local(r.OccurredUtc), r.ActorName, r.Action, r.Reference, r.Summary, r.DetailsJson, r.Hash,
            })), "audit", clock);
        });
    }

    private static int Window(int? days) => days is 7 or 30 or 90 or 365 or 3650 ? days.Value : 90;

    public sealed class ApprovalRow
    {
        public int ContractId { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Variation { get; set; }
        public string Decision { get; set; } = "";
        public string ByName { get; set; } = "";
        public string? AsBackupForName { get; set; }
        private DateTime at;
        private DateTime? sent;
        public DateTime AtUtc { get => at; set => at = DateTime.SpecifyKind(value, DateTimeKind.Utc); }
        public DateTime? SentUtc { get => sent; set => sent = value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null; }
        public string? Reason { get; set; }
        public double? TurnaroundDays => SentUtc is { } s ? Math.Round(Math.Max(0, (AtUtc - s).TotalDays), 1) : null;
    }

    private static async Task<List<ApprovalRow>> ApprovalsAsync(SqlConnection c, Person me, int? days, TimeProvider clock)
    {
        var p = ContractAccess.Params(me, new { since = clock.GetUtcNow().UtcDateTime.AddDays(-Window(days)) });
        return (await c.QueryAsync<ApprovalRow>($"""
            SELECT a.ContractId, k.Reference, k.Title, v.Number AS Variation, a.Decision, by_.Name AS ByName, gh.Name AS AsBackupForName, a.AtUtc, a.Reason, sent.OccurredUtc AS SentUtc
            FROM contracts.Approval a JOIN contracts.Contract k ON k.Id = a.ContractId JOIN contracts.Person by_ ON by_.Id = a.ById
            LEFT JOIN contracts.Person gh ON gh.Id = a.AsBackupFor LEFT JOIN contracts.Variation v ON v.Id = a.VariationId
            -- When it was sent for approval (the most recent send before the decision): the turnaround.
            OUTER APPLY (SELECT TOP (1) e.OccurredUtc FROM contracts.AuditEvent e
                         WHERE e.ContractId = a.ContractId AND e.OccurredUtc < DATEADD(SECOND, 1, a.AtUtc) -- AtUtc is to the whole second, rounded
                           AND ((a.VariationId IS NULL AND e.Action = N'contract.sent-for-approval')
                             OR (a.VariationId IS NOT NULL AND e.Action = N'variation.sent-for-approval' AND e.SubjectId = a.VariationId))
                         ORDER BY e.Id DESC) sent
            WHERE a.AtUtc >= @since AND {ContractAccess.VisibleSql("k")}
            ORDER BY a.AtUtc DESC
            """, p)).ToList();
    }

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string ActorName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? DetailsJson { get; set; }
        public string Hash { get; set; } = "";
        public int? ContractId { get; set; }
        public string? Reference { get; set; }
    }

    /// <summary>Audit events, newest first. Reports are for admins and auditors, who see every contract, Confidential ones included.</summary>
    private static async Task<List<AuditRow>> AuditAsync(SqlConnection c, int? days, string? q, TimeProvider clock, int top)
    {
        var text = (q ?? "").Trim();
        var like = $"%{text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
        return (await c.QueryAsync<AuditRow>("""
            SELECT TOP (@top) e.Id, e.OccurredUtc, e.ActorName, e.Action, e.Summary, e.DetailsJson, e.Hash, e.ContractId, k.Reference
            FROM contracts.AuditEvent e LEFT JOIN contracts.Contract k ON k.Id = e.ContractId
            WHERE e.OccurredUtc >= @since AND (@text = N'' OR e.Summary LIKE @like OR e.ActorName LIKE @like OR e.Action LIKE @like OR k.Reference LIKE @like)
            ORDER BY e.Id DESC
            """, new { top, since = clock.GetUtcNow().UtcDateTime.AddDays(-Window(days)), text, like })).ToList();
    }
}
