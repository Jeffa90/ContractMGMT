using System.Text.Json.Nodes;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Identity;
using Ecp.SharedKernel;

namespace Ecp.Notifications.Policy;

/// <summary>
/// A preview of a notification policy draft: each event with who would be told and how, and the message exactly as it would be written for a sample contract, using the organisation's own words. Notification policies are
/// not pinned: a published policy applies to every message sent after it is published, and messages already sent are never rewritten.
/// </summary>
internal sealed class NotificationPolicyFormInsight(NotificationRenderer renderer, IClassificationScaleProvider scales, ITerminologyResolver terminology) : IFormInsight
{
    public string Kind => NotificationPolicyKinds.Policy;

    public async Task<FormInsight> AnalyseAsync(Guid organisationId, JsonObject draft, JsonObject? inForce, CancellationToken ct = default)
    {
        var policy = NotificationPolicyParser.Parse(draft.ToJsonString()).Policy;
        if (policy is null) return new FormInsight([], ["The draft cannot be read yet. Fix the fields marked above."]);
        var scale = await scales.GetAsync(ct);
        var terms = await terminology.GetAsync(organisationId, ct: ct);
        var sample = new RenderContext("REF-000123", "Sample services agreement", scale.DefaultLevel, "Alex Sample", "https://example.invalid/contracts/sample", DueOn: new DateOnly(2027, 3, 31), DaysRemaining: 45, WindowDays: 90, StepName: "Commercial review",
            NowUtc: new DateTime(2027, 2, 14, 1, 0, 0, DateTimeKind.Utc), Amount: 250000m, CurrencyCode: "CHF");

        var rows = new List<IReadOnlyList<string>>();
        foreach (var e in policy.Events)
        {
            string subject = "(no message of that name)", body = "";
            if (policy.Templates.TryGetValue(e.Template, out var template))
                (subject, body) = renderer.Render(policy, template, sample, scale, terms);
            rows.Add([e.Event, string.Join(", ", e.Recipients), string.Join(" and ", e.Channels) + (e.Mandatory ? " (cannot be switched off)" : ""), subject, body]);
        }
        var previews = new List<InsightTable> { new("What would be sent", "Each message is shown for a sample contract (REF-000123, \"Sample services agreement\", 250,000 CHF, due 31 March 2027). Real messages use the real contract's details and the organisation's own date and currency formats.",
            ["Event", "Who is told", "How", "Subject", "Message"], rows) };

        var impact = new List<string>
        {
            inForce is null
                ? "No policy is in force yet. Publishing it decides who is told about what from then on."
                : "A published policy applies to every message sent after it is published. Messages already sent are not changed, and a policy is not pinned to contracts.",
        };
        var unused = policy.Templates.Keys.Except(policy.Events.Select(e => e.Template), StringComparer.Ordinal).ToList();
        if (unused.Count > 0) impact.Add("Messages no event uses: " + string.Join(", ", unused) + ".");
        var silent = PolicyVocabulary.Events.Except(policy.Events.Select(e => e.Event), StringComparer.Ordinal).ToList();
        if (silent.Count > 0) impact.Add($"{silent.Count} event(s) have no line, so nobody is told when they happen: " + string.Join(", ", silent.Take(8)) + (silent.Count > 8 ? ", and more." : "."));
        if (inForce is not null && NotificationPolicyParser.Parse(inForce.ToJsonString()).Policy is { } before)
        {
            var added = policy.Events.Select(e => e.Event).Except(before.Events.Select(e => e.Event)).ToList();
            var removed = before.Events.Select(e => e.Event).Except(policy.Events.Select(e => e.Event)).ToList();
            if (added.Count > 0) impact.Add("People will start to be told about: " + string.Join(", ", added) + ".");
            if (removed.Count > 0) impact.Add("People will stop being told about: " + string.Join(", ", removed) + ".");
            foreach (var e in policy.Events)
                if (before.RuleFor(e.Event) is { } was && (!was.Recipients.SequenceEqual(e.Recipients) || !was.Channels.SequenceEqual(e.Channels)))
                    impact.Add($"For {e.Event}, who is told or how changes.");
        }
        return new FormInsight(previews, impact);
    }
}
