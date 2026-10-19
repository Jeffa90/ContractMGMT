using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// G-34, end to end through the distributed artefact: the pack tool is packed as a .NET tool package, installed into an empty folder from that package alone, and run from a working folder that contains only a copy of one pack
/// source. It builds, validates, reports, signs (from an external key reference), verifies and inspects a pack, and every refusal has its stable exit code. Nothing here calls repository scripts or the repository's build output.
/// In CI the exact package that is published is used (<c>ECP_PACK_TOOL_PACKAGE_DIR</c>); locally the test packs one itself.
/// </summary>
[Trait("Category", "PackTool")]
public sealed class PackToolArtefactTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-pack-tool-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a temporary folder; left for the system to clear */ } }

    private sealed record Run(int Exit, string Out, string Err);

    private static Run Exec(string exe, string[] args, string cwd, Dictionary<string, string?>? env = null, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; info.Environment["DOTNET_NOLOGO"] = "1"; info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var (k, v) in env ?? []) { if (v is null) info.Environment.Remove(k); else info.Environment[k] = v; }
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds)) { p.Kill(true); throw new TimeoutException($"{exe} {string.Join(' ', args)} did not finish."); }
        return new Run(p.ExitCode, stdout.Result, stderr.Result);
    }

    private string InstallTool()
    {
        var repo = RepoRoot.Find();
        var package = Environment.GetEnvironmentVariable("ECP_PACK_TOOL_PACKAGE_DIR");
        if (string.IsNullOrWhiteSpace(package))
        {
            package = Path.Combine(_root, "feed");
            var packed = Exec("dotnet", ["pack", Path.Combine(repo, "src", "Ecp.PackTool", "Ecp.PackTool.csproj"), "-c", "Release", "-o", package, "--nologo", "-v", "q"], repo);
            Assert.True(packed.Exit == 0, "dotnet pack failed: " + packed.Out + packed.Err);
        }
        var feed = Path.GetFullPath(package);
        var nupkg = Directory.GetFiles(feed, "Ecp.PackTool.*.nupkg").Single();
        var version = Path.GetFileNameWithoutExtension(nupkg)["Ecp.PackTool.".Length..];
        // A feed that holds nothing but the tool package: the install cannot reach the repository or anything built from it.
        var config = Path.Combine(_root, "nuget.config");
        File.WriteAllText(config, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /><add key=\"publisher\" value=\"{feed}\" /></packageSources></configuration>");
        var tools = Path.Combine(_root, "tools");
        var install = Exec("dotnet", ["tool", "install", "Ecp.PackTool", "--tool-path", tools, "--configfile", config, "--version", version, "--ignore-failed-sources"], _root);
        Assert.True(install.Exit == 0, "dotnet tool install failed: " + install.Out + install.Err);
        var exe = Path.Combine(tools, OperatingSystem.IsWindows() ? "ecp-pack.exe" : "ecp-pack");
        Assert.True(File.Exists(exe), "The installed tool was not found at " + exe);
        return exe;
    }

    [Fact]
    public void A_pack_publisher_can_build_validate_report_sign_verify_and_inspect_from_the_installed_tool_alone()
    {
        var tool = InstallTool();
        var work = Directory.CreateDirectory(Path.Combine(_root, "publisher")).FullName;
        CopyDirectory(Path.Combine(RepoRoot.Find(), "templates", "packs", "construction-subcontracts"), Path.Combine(work, "my-pack"));
        Run Ecp(params string[] args) => Exec(tool, args, work);

        var version = Ecp("version");
        Assert.Equal(0, version.Exit);
        Assert.StartsWith("ecp-pack ", version.Out);

        // Usage mistakes are exit 2 and say what is wrong in words.
        Assert.Equal(2, Ecp().Exit);
        Assert.Equal(2, Ecp("frobnicate").Exit);
        var missing = Ecp("build", "--source", "my-pack");
        Assert.Equal(2, missing.Exit);
        Assert.Contains("Missing --out", missing.Err);

        // Build twice: the same source gives the same bytes, so a package can be reproduced and compared.
        Assert.Equal(0, Ecp("build", "--source", "my-pack", "--out", "out/a.ecpkg").Exit);
        Assert.Equal(0, Ecp("build", "--source", "my-pack", "--out", "out/b.ecpkg").Exit);
        Assert.Equal(File.ReadAllBytes(Path.Combine(work, "out", "a.ecpkg")), File.ReadAllBytes(Path.Combine(work, "out", "b.ecpkg")));
        Assert.Equal(0, Ecp("compare", "--a", "out/a.ecpkg", "--b", "out/b.ecpkg").Exit);

        var validate = Ecp("validate", "--package", "out/a.ecpkg");
        Assert.Equal(0, validate.Exit);
        Assert.Contains("valid", validate.Out);

        // The sanitised report: identity, hashes and findings; no content, no key, no signature bytes.
        var report = Ecp("report", "--package", "out/a.ecpkg", "--out", "out/report.json");
        Assert.Equal(0, report.Exit);
        var reportText = File.ReadAllText(Path.Combine(work, "out", "report.json"));
        using (var doc = JsonDocument.Parse(reportText))
        {
            Assert.True(doc.RootElement.GetProperty("validation").GetProperty("valid").GetBoolean());
            Assert.Equal("a.ecpkg", doc.RootElement.GetProperty("package").GetProperty("file").GetString());
            Assert.False(doc.RootElement.GetProperty("signature").GetProperty("present").GetBoolean());
        }
        Assert.DoesNotContain(work, reportText);                  // no path of the publisher's machine

        // Keys are made by the publisher and referenced by the tool.
        var keygen = Ecp("keygen", "--out-private", "keys/publisher.key", "--out-public", "keys/publisher.pub.pem");
        Assert.True(keygen.Exit == 0, keygen.Out + keygen.Err);
        var privatePem = File.ReadAllText(Path.Combine(work, "keys", "publisher.key"));
        var privateBody = string.Concat(privatePem.Split('\n').Where(l => !l.StartsWith("-----"))).Trim();
        Assert.Equal(1, Ecp("keygen", "--out-private", "keys/publisher.key", "--out-public", "keys/publisher.pub.pem").Exit);   // never overwrites a key

        // Signing from a file reference, then from an environment variable.
        var signedByFile = Exec(tool, ["sign", "--package", "out/a.ecpkg", "--out", "out/signed-file.ecpkg", "--key-id", "publisher-2026", "--key-ref", "file:keys/publisher.key"], work);
        Assert.True(signedByFile.Exit == 0, signedByFile.Out + signedByFile.Err);
        var signedByEnv = Exec(tool, ["sign", "--package", "out/a.ecpkg", "--out", "out/signed-env.ecpkg", "--key-id", "publisher-2026", "--key-ref", "env:ECP_E2E_PACK_KEY"], work, new() { ["ECP_E2E_PACK_KEY"] = privatePem });
        Assert.True(signedByEnv.Exit == 0, signedByEnv.Out + signedByEnv.Err);

        var verify = Ecp("verify", "--package", "out/signed-file.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production");
        Assert.Equal(0, verify.Exit);
        Assert.Contains("valid", verify.Out);
        Assert.Equal(0, Ecp("verify", "--package", "out/signed-env.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production").Exit);
        var inspect = Ecp("inspect", "--package", "out/signed-file.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production");
        Assert.Equal(0, inspect.Exit);
        Assert.Contains("construction-subcontracts", inspect.Out);
        var signedReport = Ecp("report", "--package", "out/signed-file.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production");
        Assert.Equal(0, signedReport.Exit);
        Assert.Contains("publisher-2026", signedReport.Out);

        // The unsigned package is refused for Production; a different key does not verify; a tampered package does not verify.
        Assert.Equal(1, Ecp("verify", "--package", "out/a.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production").Exit);
        Assert.Equal(0, Ecp("keygen", "--out-private", "keys/other.key", "--out-public", "keys/other.pub.pem").Exit);
        Assert.Equal(1, Ecp("verify", "--package", "out/signed-file.ecpkg", "--public-key", "keys/other.pub.pem", "--key-id", "publisher-2026", "--production").Exit);
        var tampered = File.ReadAllBytes(Path.Combine(work, "out", "signed-file.ecpkg"));
        tampered[tampered.Length / 2] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(work, "out", "tampered.ecpkg"), tampered);
        Assert.NotEqual(0, Ecp("verify", "--package", "out/tampered.ecpkg", "--public-key", "keys/publisher.pub.pem", "--key-id", "publisher-2026", "--production").Exit);

        // Environment failures are exit 3 and say which reference was the problem, never the key.
        var noKey = Exec(tool, ["sign", "--package", "out/a.ecpkg", "--out", "out/x.ecpkg", "--key-id", "publisher-2026", "--key-ref", "env:ECP_E2E_NOT_SET"], work);
        Assert.Equal(3, noKey.Exit);
        Assert.Contains("ECP_E2E_NOT_SET", noKey.Err);
        Assert.Equal(3, Ecp("sign", "--package", "out/a.ecpkg", "--out", "out/x.ecpkg", "--key-id", "publisher-2026", "--key-ref", "file:keys/missing.key").Exit);
        File.WriteAllText(Path.Combine(work, "keys", "garbage.key"), "this is not a key");
        Assert.Equal(3, Ecp("sign", "--package", "out/a.ecpkg", "--out", "out/x.ecpkg", "--key-id", "publisher-2026", "--key-ref", "file:keys/garbage.key").Exit);
        Assert.Equal(3, Ecp("sign", "--package", "out/a.ecpkg", "--out", "out/x.ecpkg", "--key-id", "publisher-2026", "--key-ref", "keyvault:publisher").Exit);
        Assert.Equal(2, Ecp("sign", "--package", "out/a.ecpkg", "--out", "out/x.ecpkg", "--key-id", "publisher-2026").Exit);     // no key reference at all: a usage error
        Assert.Equal(3, Ecp("validate", "--package", "out/does-not-exist.ecpkg").Exit);
        Assert.False(File.Exists(Path.Combine(work, "out", "x.ecpkg")));                                                        // a failed signing leaves nothing behind

        // The private key never appears in anything the tool printed or wrote for sharing.
        var everything = string.Join('\n', new[] { version.Out, validate.Out, report.Out, reportText, keygen.Out, signedByFile.Out, signedByFile.Err, signedByEnv.Out, verify.Out, inspect.Out, signedReport.Out, noKey.Err });
        Assert.DoesNotContain("PRIVATE KEY", everything);
        Assert.DoesNotContain(privateBody, everything);
        Assert.DoesNotContain(privateBody, reportText);

        // The tool was installed from the package: its files are in the temporary tool folder, not in the repository.
        Assert.StartsWith(_root, tool);
        Assert.DoesNotContain(RepoRoot.Find(), string.Join(';', Directory.GetFiles(Path.GetDirectoryName(tool)!, "*.json", SearchOption.AllDirectories)));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to), overwrite: true);
    }
}
