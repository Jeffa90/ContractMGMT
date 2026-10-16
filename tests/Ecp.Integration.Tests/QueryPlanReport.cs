using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Ecp.Integration.Tests;

/// <summary>
/// PER-002 evidence on a real SQL Server: for an operation, the statements it sent (so a repeated statement, an N+1, is visible), what each cost (elapsed, CPU, logical reads, rows), and the plan as a
/// digest of operators with their estimated and actual rows, the object read, the residual predicate, missing-index suggestions, warnings and the compiled and runtime parameter values. Plans come from the
/// plan cache of the disposable test server (<c>sys.dm_exec_query_stats</c> with <c>sys.dm_exec_query_plan_stats</c>, the last actual plan), so it needs a SQL Server 2019 or later and is a diagnostic: it asserts
/// nothing and is written to <c>artifacts/performance/plans-sqlserver.txt</c>. It never runs against anything but the test server named by <c>ECP_TEST_SQL</c>.
/// </summary>
internal static class QueryPlanReport
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static async Task EnableAsync(string connectionString)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "ALTER DATABASE SCOPED CONFIGURATION SET LAST_QUERY_PLAN_STATS = ON";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql; cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Empties the plan cache of the disposable test server so the next statement compiles afresh (used to look at parameter sensitivity).</summary>
    public static Task ForgetPlansAsync(string connectionString) => ExecAsync(connectionString, "DBCC FREEPROCCACHE");

    public static async Task<string> CaptureAsync(string connectionString, string label, Func<Task> work)
    {
        await ForgetPlansAsync(connectionString);
        var sw = Stopwatch.StartNew();
        await work();
        sw.Stop();
        var sb = new StringBuilder();
        sb.AppendLine($"=== {label}  (one cold run, {sw.Elapsed.TotalMilliseconds:0.0} ms including compilation)");

        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = @"
SELECT qs.execution_count, qs.last_elapsed_time, qs.last_worker_time, qs.last_logical_reads, qs.last_rows,
       SUBSTRING(st.text, (qs.statement_start_offset/2)+1, ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1) AS stmt,
       COALESCE(ps.query_plan, qp.query_plan) AS plan_xml,
       CASE WHEN ps.query_plan IS NULL THEN 'estimated' ELSE 'last actual' END AS plan_kind
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
OUTER APPLY sys.dm_exec_query_plan_stats(qs.plan_handle) ps
OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
WHERE st.text NOT LIKE '%dm_exec_query_stats%' AND st.text NOT LIKE '%sp_set_session_context%' AND st.text NOT LIKE '%FREEPROCCACHE%'
ORDER BY qs.last_elapsed_time DESC";
        var statements = new List<(long Count, long Elapsed, long Cpu, long Reads, long Rows, string Text, string? Plan, string Kind)>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                statements.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));

        var repeated = statements.Where(s => s.Count > 1).ToList();
        sb.AppendLine($"statements: {statements.Count} distinct, {statements.Sum(s => s.Count)} executions; repeated within the run (N+1 suspects): {repeated.Count}");
        foreach (var s in repeated) sb.AppendLine($"  x{s.Count}: {Shorten(s.Text, 140)}");
        foreach (var s in statements.Take(6))
        {
            sb.AppendLine($"--- {s.Elapsed / 1000.0:0.0} ms elapsed, {s.Cpu / 1000.0:0.0} ms CPU, {s.Reads} logical reads, {s.Rows} rows, {s.Kind} plan");
            sb.AppendLine("    sql: " + Shorten(s.Text, 700));
            if (s.Plan is not null) sb.Append(Digest(s.Plan));
        }
        return sb.ToString();
    }

    private static string Shorten(string text, int max)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + " ...";
    }

    private static string Digest(string xml)
    {
        var sb = new StringBuilder();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch (Exception e) { return "    (plan could not be read: " + e.Message + ")\n"; }
        foreach (var stmt in doc.Descendants(P + "StmtSimple").Take(1))
        {
            var qp = stmt.Element(P + "QueryPlan");
            if (qp is null) continue;
            var time = qp.Element(P + "QueryTimeStats");
            sb.AppendLine($"    plan: optimisation={stmt.Attribute("StatementOptmLevel")?.Value}, cardinality model={stmt.Attribute("CardinalityEstimationModelVersion")?.Value}, subtree cost={stmt.Attribute("StatementSubTreeCost")?.Value}, "
                + $"elapsed={time?.Attribute("ElapsedTime")?.Value} ms, cpu={time?.Attribute("CpuTime")?.Value} ms, dop={qp.Attribute("DegreeOfParallelism")?.Value}, memory grant KB={qp.Element(P + "MemoryGrantInfo")?.Attribute("GrantedMemory")?.Value}");
            foreach (var m in qp.Descendants(P + "MissingIndex"))
                sb.AppendLine($"    MISSING INDEX {m.Attribute("Schema")?.Value}.{m.Attribute("Table")?.Value}: " + string.Join("; ", m.Elements(P + "ColumnGroup").Select(g => g.Attribute("Usage")?.Value + "(" + string.Join(",", g.Elements(P + "Column").Select(x => x.Attribute("Name")?.Value)) + ")")));
            foreach (var w in qp.Elements(P + "Warnings").Take(1)) sb.AppendLine("    WARNINGS: " + string.Join(", ", w.Attributes().Select(a => a.Name.LocalName + "=" + a.Value)) + " " + string.Join(", ", w.Elements().Select(e => e.Name.LocalName)));
            foreach (var p in qp.Element(P + "ParameterList")?.Elements(P + "ColumnReference") ?? [])
                sb.AppendLine($"    param {p.Attribute("Column")?.Value} {p.Attribute("ParameterDataType")?.Value} compiled={Shorten(p.Attribute("ParameterCompiledValue")?.Value ?? "-", 60)} runtime={Shorten(p.Attribute("ParameterRuntimeValue")?.Value ?? "-", 60)}");
            var ops = qp.Descendants(P + "RelOp").ToList();
            foreach (var op in ops.Take(90))
            {
                var depth = op.Ancestors(P + "RelOp").Count();
                var counters = op.Element(P + "RunTimeInformation")?.Elements(P + "RunTimeCountersPerThread").ToList() ?? [];
                long actual = counters.Sum(x => long.TryParse(x.Attribute("ActualRows")?.Value, out var v) ? v : 0);
                long execs = counters.Sum(x => long.TryParse(x.Attribute("ActualExecutions")?.Value, out var v) ? v : 0);
                long reads = counters.Sum(x => long.TryParse(x.Attribute("ActualLogicalReads")?.Value, out var v) ? v : 0);
                var own = op.Elements().Where(e => e.Name != P + "OutputList" && e.Name != P + "Warnings" && e.Name != P + "RunTimeInformation").ToList();
                var obj = own.SelectMany(e => e.Elements(P + "Object")).FirstOrDefault();
                var pred = own.SelectMany(e => e.Elements(P + "Predicate")).Select(x => x.Element(P + "ScalarOperator")?.Attribute("ScalarString")?.Value).FirstOrDefault();
                var seek = own.SelectMany(e => e.Elements(P + "SeekPredicates")).Select(x => x.Descendants(P + "ScalarOperator").FirstOrDefault()?.Attribute("ScalarString")?.Value).FirstOrDefault();
                sb.Append($"    {new string(' ', depth * 2)}{op.Attribute("PhysicalOp")?.Value} ({op.Attribute("LogicalOp")?.Value}) est={op.Attribute("EstimateRows")?.Value} est.exec={op.Attribute("EstimateExecutions")?.Value ?? "1"} actual={actual} exec={execs} reads={reads} cost={op.Attribute("EstimatedTotalSubtreeCost")?.Value}");
                if (obj is not null) sb.Append($" on {obj.Attribute("Table")?.Value}{(obj.Attribute("Index") is { } ix ? "." + ix.Value : "")}");
                if (seek is not null) sb.Append(" SEEK " + Shorten(seek, 160));
                if (pred is not null) sb.Append(" WHERE " + Shorten(pred, 200));
                sb.AppendLine();
            }
            if (ops.Count > 90) sb.AppendLine($"    ... {ops.Count - 90} more operators");
        }
        return sb.ToString();
    }
}
