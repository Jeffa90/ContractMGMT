using System.Text.Json.Nodes;

namespace Ecp.Configuration.Forms;

public sealed record InsightTable(string Title, string? Help, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// What a form shows beside the fields so that a change can be understood before it is submitted: a preview of what the draft would do (how it reads, what a sample message looks like), and what it would affect that
/// already exists. Reads only; it never changes a draft, a release or a record.
/// </summary>
public sealed record FormInsight(IReadOnlyList<InsightTable> Previews, IReadOnlyList<string> Impact, string? SimulatorLink = null);

public interface IFormInsight
{
    string Kind { get; }
    Task<FormInsight> AnalyseAsync(Guid organisationId, JsonObject draft, JsonObject? inForce, CancellationToken ct = default);
}
