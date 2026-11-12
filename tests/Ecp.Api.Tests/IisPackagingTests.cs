using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecp.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The IIS web.config and the application must agree on request limits, and no endpoint may be opened wider than its purpose. These tests read the
/// source web.config and the page models; the published output is checked by tools/check-iis-publish.py. None of this proves an IIS run.
/// </summary>
[Trait("Category", "Api")]
public class IisPackagingTests
{
    private static string Repo(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative)) || Directory.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    private static long Limit(Type model) => ((IRequestSizeLimitMetadata)model.GetCustomAttribute<RequestSizeLimitAttribute>(inherit: true)!).MaxRequestBodySize ?? -1;

    private static XDocument WebConfig() => XDocument.Load(Repo("src/Ecp.Host/web.config"));

    private static Dictionary<string, long> WebConfigLimits()
        => WebConfig().Descendants("location").Where(l => l.Descendants("requestLimits").Any())
            .ToDictionary(l => (string?)l.Attribute("path") ?? ".", l => long.Parse((string)l.Descendants("requestLimits").First().Attribute("maxAllowedContentLength")!));

    [Fact]
    public void The_web_config_uses_the_V2_module_in_process_with_no_secrets_or_stdout_logging()
    {
        var doc = WebConfig();
        Assert.Contains(doc.Descendants("add"), a => (string?)a.Attribute("name") == "aspNetCore" && (string?)a.Attribute("modules") == "AspNetCoreModuleV2");
        var asp = Assert.Single(doc.Descendants("aspNetCore"));
        Assert.Equal("inprocess", (string?)asp.Attribute("hostingModel"));
        Assert.Equal("dotnet", (string?)asp.Attribute("processPath"));
        Assert.Contains("Ecp.Host.dll", (string?)asp.Attribute("arguments"));
        Assert.Equal("false", (string?)asp.Attribute("stdoutLogEnabled"));
        Assert.Empty(asp.Descendants("environmentVariable")); // the deploy script stamps non-secret values; secrets are never written here
        var text = File.ReadAllText(Repo("src/Ecp.Host/web.config"));
        Assert.DoesNotMatch(new Regex("password|secret|connectionstring", RegexOptions.IgnoreCase), string.Join("\n", doc.Descendants().Where(e => !e.HasElements).Select(e => e.ToString())));
        Assert.Contains("X-Powered-By", text);
    }

    [Fact]
    public void The_project_publishes_the_web_config_and_pins_the_hosting_model()
    {
        var csproj = File.ReadAllText(Repo("src/Ecp.Host/Ecp.Host.csproj"));
        Assert.Contains("<AspNetCoreHostingModel>InProcess</AspNetCoreHostingModel>", csproj);
        Assert.Contains("<AspNetCoreModuleName>AspNetCoreModuleV2</AspNetCoreModuleName>", csproj);
        Assert.DoesNotContain("web.config\" CopyToPublishDirectory=\"Never", csproj);
    }

    [Fact]
    public void The_default_limit_is_small_and_matches_the_application_default()
    {
        var limits = WebConfigLimits();
        Assert.Equal(UploadLimits.DefaultRequestBytes, limits["."]);
        Assert.All(limits.Where(l => l.Key != "."), l => Assert.True(l.Value > limits["."], $"{l.Key} should only exist to raise the limit"));
    }

    /// <summary>Every page that accepts a file: its model type, the URL prefix IIS sees (the page route without its parameters).</summary>
    private static IEnumerable<(Type Model, string Url)> UploadPages()
    {
        var pagesRoot = Repo("src/Ecp.Host/Pages");
        foreach (var type in typeof(UploadLimits).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(PageModel).IsAssignableFrom(t)))
        {
            var accepts = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name.StartsWith("OnPost") && m.GetParameters().Any(p => p.ParameterType == typeof(IFormFile)));
            if (!accepts) continue;
            var relative = type.Namespace!.Replace("Ecp.Host.Pages", "").Trim('.').Replace('.', '/');
            var name = type.Name[..^"Model".Length];
            var file = Path.Combine(pagesRoot, relative, name + ".cshtml");
            var route = Regex.Match(File.ReadAllText(file), "^@page \"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value;
            yield return (type, string.Join('/', route.Trim('/').Split('/').TakeWhile(s => !s.StartsWith('{'))));
        }
    }

    [Fact]
    public void Every_page_that_accepts_a_file_declares_its_own_limit_and_a_matching_IIS_location()
    {
        var limits = WebConfigLimits();
        var pages = UploadPages().ToList();
        Assert.True(pages.Count >= 6, "expected the document, pack, mapping and migration upload pages");
        foreach (var (model, url) in pages)
        {
            var attr = model.GetCustomAttribute<RequestSizeLimitAttribute>(inherit: true);
            Assert.True(attr is not null, $"{model.Name} accepts a file but has no [RequestSizeLimit]");
            Assert.NotNull(model.GetCustomAttribute<RequestTimeoutAttribute>(inherit: true));
            // IIS applies the longest matching location, falling back to the site default.
            var iis = limits.Where(l => l.Key != "." && (url.Equals(l.Key, StringComparison.OrdinalIgnoreCase) || url.StartsWith(l.Key + "/", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(l => l.Key.Length).Select(l => (long?)l.Value).FirstOrDefault() ?? limits["."];
            Assert.True(iis >= Limit(model), $"{model.Name}: IIS allows {iis} bytes at /{url} but the page allows {Limit(model)}");
            Assert.True(iis <= Limit(model) + 1024 * 1024 || iis == limits["."], $"{model.Name}: IIS opens /{url} far wider ({iis}) than the page needs ({Limit(model)})");
        }
    }

    [Fact]
    public void Only_the_migration_archive_endpoint_is_opened_to_two_gibibytes()
    {
        var huge = UploadPages().Where(p => Limit(p.Model) > 128L * 1024 * 1024).ToList();
        var only = Assert.Single(huge);
        Assert.Equal("Migration/Batches", only.Url);
        Assert.Equal(UploadLimits.MigrationArchiveRequestBytes, Limit(only.Model));
        Assert.Equal(["Migration/Batches"], WebConfigLimits().Where(l => l.Value > 128L * 1024 * 1024).Select(l => l.Key).ToArray());
        Assert.Equal(2147483648, WebConfigLimits()["Migration/Batches"]);
    }

    [Fact]
    public void Documents_up_to_the_configured_document_limit_fit_the_endpoint_limit()
    {
        var documents = new Ecp.Documents.DocumentsOptions();
        Assert.True(UploadLimits.DocumentRequestBytes > documents.MaxFileBytes, "the request limit must leave room for the multipart framing");
        Assert.True(UploadLimits.DocumentRequestBytes - documents.MaxFileBytes <= 2 * 1024 * 1024);
        Assert.True(UploadLimits.PackRequestBytes > 8 * 1024 * 1024);
    }

    [Fact]
    public void The_timeout_policy_is_explicit_and_longest_for_migration()
    {
        Assert.True(UploadLimits.DefaultTimeout < UploadLimits.UploadTimeout);
        Assert.True(UploadLimits.UploadTimeout < UploadLimits.MigrationTimeout);
        var program = File.ReadAllText(Repo("src/Ecp.Host/Program.cs"));
        Assert.Contains("AddRequestTimeouts", program);
        Assert.Contains("UseRequestTimeouts", program);
    }
}
