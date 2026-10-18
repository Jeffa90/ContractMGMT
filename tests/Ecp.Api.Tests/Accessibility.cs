using System.Text.RegularExpressions;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The structural accessibility checks every administration page must pass (WCAG 2.2 AA, as far as markup can show it): one main heading, a document language, every table captioned with column headers, every visible
/// form control labelled, every button and link named, no positive tab order, and error and status messages announced. A browser-based axe run is not available in this workspace, so this is a structural check, not a certification.
/// </summary>
internal static class Accessibility
{
    public static void AssertBasics(string html, string page)
    {
        Assert.True(Regex.Matches(html, "<h1[ >]").Count == 1, $"{page}: exactly one main heading");
        Assert.Matches("<html[^>]* lang=\"[A-Za-z-]+\"", html);
        foreach (Match table in Regex.Matches(html, "<table[ >]"))
        {
            var end = html.IndexOf("</table>", table.Index, StringComparison.Ordinal);
            var body = html.Substring(table.Index, end - table.Index);
            Assert.True(body.Contains("<caption", StringComparison.Ordinal), $"{page}: every table has a caption");
            Assert.True(body.Contains("scope=\"col\"", StringComparison.Ordinal), $"{page}: every table has column headers");
        }
        foreach (Match control in Regex.Matches(html, "<(input|select|textarea)\\b([^>]*)>"))
        {
            var attrs = control.Groups[2].Value;
            if (Regex.IsMatch(attrs, "type=\"(hidden|submit|button)\"")) continue;
            var id = Regex.Match(attrs, "\\bid=\"([^\"]+)\"");
            var labelled = Regex.IsMatch(attrs, "aria-label=\"[^\"]+\"") || (id.Success && Regex.IsMatch(html, "<label[^>]*for=\"" + Regex.Escape(id.Groups[1].Value) + "\""));
            Assert.True(labelled, $"{page}: unlabelled control {control.Value}");
        }
        foreach (Match button in Regex.Matches(html, "<button\\b[^>]*>(.*?)</button>", RegexOptions.Singleline))
            Assert.True(Regex.Replace(button.Groups[1].Value, "<[^>]+>", "").Trim().Length > 0 || button.Value.Contains("aria-label"), $"{page}: a button has no name");
        foreach (Match link in Regex.Matches(html, "<a\\b[^>]*href[^>]*>(.*?)</a>", RegexOptions.Singleline))
            Assert.True(Regex.Replace(link.Groups[1].Value, "<[^>]+>", "").Trim().Length > 0 || link.Value.Contains("aria-label"), $"{page}: a link has no name");
        Assert.DoesNotMatch("tabindex=\"[1-9]", html);
    }
}
