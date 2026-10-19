using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ecp.Packs;

/// <summary>
/// The command-line surface of the pack tool (<c>ecp-pack</c>). It lives in the library so the same code is run by the tool and by the tests. Output is sanitised: no key material, no signature bytes, no
/// server paths beyond the ones the operator typed. Exit codes (stable, documented in docs/PACK_SIGNING.md): 0 success; 1 the package or request is not acceptable (invalid, untrusted, not reproducible); 2 usage error;
/// 3 the environment is at fault (a file or key could not be read or written, or a key reference could not be resolved).
/// </summary>
public static class PackCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment = null, Func<DateTime>? clock = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        clock ??= () => DateTime.UtcNow;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { output.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        if (args[0] is "version" or "--version") return Version(output);
        var command = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) { error.WriteLine($"Unexpected argument '{args[i]}'."); return 2; }
            var name = args[i];
            if (name is "--production" or "--allow-unsigned-synthetic" or "--json" or "--force") { flags.Add(name); continue; }
            if (i + 1 >= args.Length) { error.WriteLine($"{name} needs a value."); return 2; }
            options[name] = args[++i];
        }
        string? Opt(string n) => options.TryGetValue(n, out var v) ? v : null;
        string? Need(string n) { var v = Opt(n); if (v is null) error.WriteLine($"Missing {n}."); return v; }

        try
        {
            switch (command)
            {
                case "report": return Report(Need("--package"), Opt("--out"), Opt("--platform-version"), Opt("--trust-file"), Opt("--public-key"), Opt("--key-id"), flags, output, error, clock);
                case "build": return Build(Need("--source"), Need("--out"), output, error);
                case "build-all": return BuildAll(Need("--source"), Need("--out"), output, error);
                case "validate": return Validate(Need("--package"), Opt("--platform-version"), output, error);
                case "hash": return Hash(Need("--package"), output, error);
                case "inspect": return Inspect(Need("--package"), Opt("--trust-file"), Opt("--public-key"), Opt("--key-id"), flags, output, error, clock);
                case "sign": return Sign(Need("--package"), Need("--out"), Opt("--key-ref"), Opt("--key-file"), Opt("--key-env"), Need("--key-id"), output, error, environment, clock);
                case "verify": return VerifyCommand(Need("--package"), Opt("--trust-file"), Opt("--public-key"), Opt("--key-id"), flags, output, error, clock);
                case "compare": return Compare(Need("--a"), Need("--b"), output, error);
                case "keygen": return KeyGen(Need("--out-private"), Need("--out-public"), flags, output, error);
                default: error.WriteLine($"Unknown command '{command}'."); error.WriteLine(Usage); return 2;
            }
        }
        catch (KeyReferenceException ex) { error.WriteLine("The key could not be used: " + ex.Message); return 3; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            error.WriteLine("The command failed because a file or key could not be read or written: " + Safe(ex.Message));
            return 3;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            error.WriteLine("The command failed: " + Safe(ex.Message));
            return 1;
        }
    }

    public const string Usage = """
        ecp-pack: build, inspect, sign and verify solution packs (.ecpkg). A technical tool for the Pack Publisher; it needs no source checkout and no Visual Studio.

          version                                                  tool version and the pack format it reads and writes
          build     --source <pack-source-dir> --out <file.ecpkg>  build a package from a pack source folder (the same input gives the same bytes)
          build-all --source <dir-of-pack-sources> --out <output-dir>
          validate  --package <file> [--platform-version x.y.z]    structure, hashes and compatibility
          report    --package <file> [--out report.json] [--platform-version x.y.z] [trust options]   sanitised validation report (JSON): no content, no key, no signature bytes
          hash      --package <file>
          inspect   --package <file> [--trust-file keys.json | --public-key key.pem --key-id id] [--production] [--allow-unsigned-synthetic]
          sign      --package <file> --out <file> --key-id <id> --key-ref <env:VARIABLE | file:/path/to/private.pem>
          verify    --package <file> (--trust-file keys.json | --public-key key.pem --key-id id) [--production] [--allow-unsigned-synthetic]
          compare   --a <file> --b <file>      (reproducibility: equal when everything except the signature is identical)
          keygen    --out-private <file> --out-public <file>      (writes a NEW key pair; keep the private key out of source control)

        The private key is referenced, never typed: env:VARIABLE names an environment variable that holds the key in PEM form, file:PATH names a PEM file (--key-file and --key-env remain as shorter spellings).
        The key is read only to sign; this tool never prints, stores or uploads it. Signing is done where the key is kept (a build agent or the publisher's machine), never in a browser.

        Exit codes: 0 success; 1 the package or request is not acceptable; 2 usage error (a missing or unknown option); 3 a file or key could not be read or written, or a key reference could not be resolved.
        """;

    private static string Safe(string message) => message.Length > 300 ? message[..300] : message;

    private static OpenedPackage Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new IOException($"The file '{Path.GetFileName(path)}' was not found.");
        if (info.Length > PackLimits.Default.MaxPackageBytes) throw new IOException("The file is larger than a package may be.");
        return PackReader.Open(File.ReadAllBytes(path));
    }

    private static void PrintIssues(IEnumerable<PackIssue> issues, TextWriter w)
    {
        foreach (var i in issues.Where(i => i.Severity != PackSeverity.Info).OrderByDescending(i => i.Severity))
            w.WriteLine($"  {(i.IsError ? "ERROR" : "warning")} [{i.Code}] {i.Message}{(i.Entry is null ? "" : $" ({i.Entry})")}{(i.Remediation is null ? "" : $" Fix: {i.Remediation}")}");
    }

    private static int Build(string? source, string? outPath, TextWriter output, TextWriter error)
    {
        if (source is null || outPath is null) return 2;
        var result = PackBuilder.Build(source);
        if (!result.Succeeded) { error.WriteLine("The pack could not be built:"); PrintIssues(result.Issues, error); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, result.Package!);
        output.WriteLine($"Built {result.Opened!.Manifest!.PackKey} {result.Opened.Manifest.Version}: {Path.GetFileName(outPath)}");
        output.WriteLine($"SHA-256:        {result.Opened.Sha256}");
        output.WriteLine($"Content digest: {result.Opened.ContentDigest}");
        output.WriteLine("The package is NOT signed. Sign it with 'ecp-pack sign' before using it in Production.");
        return 0;
    }

    private static int BuildAll(string? source, string? outDir, TextWriter output, TextWriter error)
    {
        if (source is null || outDir is null) return 2;
        var folders = Directory.EnumerateDirectories(source).Where(d => File.Exists(Path.Combine(d, PackBuilder.RecipeFile))).OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (folders.Count == 0) { error.WriteLine("No pack sources were found (a pack source folder holds a pack-source.json)."); return 1; }
        var failed = 0;
        foreach (var folder in folders)
        {
            var result = PackBuilder.Build(folder);
            if (!result.Succeeded) { error.WriteLine($"{Path.GetFileName(folder)}: the pack could not be built:"); PrintIssues(result.Issues, error); failed++; continue; }
            var m = result.Opened!.Manifest!;
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"{m.PackKey}-{m.Version}.ecpkg"), result.Package!);
            output.WriteLine($"Built {m.PackKey} {m.Version}  sha256 {result.Opened.Sha256}");
        }
        return failed == 0 ? 0 : 1;
    }

    private static int Validate(string? path, string? platform, TextWriter output, TextWriter error)
    {
        if (path is null) return 2;
        var package = Load(path);
        var issues = new List<PackIssue>(package.Issues);
        var version = platform is not null ? SemVer.Parse(platform) : PlatformVersion.Current;
        issues.AddRange(PackReader.CheckCompatibility(package, new PackCompatibilityContext(version, PackKinds.Packable.ToHashSet(StringComparer.Ordinal))));
        if (package.Manifest is { } m) issues.AddRange(PackBuilder.CrossCheckSolutionPack(package, m));
        var errors = issues.Count(i => i.IsError);
        output.WriteLine(errors == 0 ? "The package structure, hashes and compatibility are valid." : $"The package is not valid ({errors} error(s)).");
        PrintIssues(issues, errors == 0 ? output : error);
        return errors == 0 ? 0 : 1;
    }

    private static int Version(TextWriter output)
    {
        var assembly = typeof(PackCli).Assembly;
        var version = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";
        output.WriteLine($"ecp-pack {version}");
        output.WriteLine($"Platform version this tool checks packs against: {PlatformVersion.Current}");
        output.WriteLine($"Signature algorithm: {SignatureAlgorithms.Es256}");
        return 0;
    }

    /// <summary>
    /// A validation report that is safe to send to someone else: the package's identity and hashes, every finding with its stable code, the signature facts (key id and algorithm, never the signature bytes), and nothing from inside
    /// the content. It is a statement about the package, not an approval of it.
    /// </summary>
    private static int Report(string? path, string? outPath, string? platform, string? trustFile, string? publicKey, string? keyId, HashSet<string> flags, TextWriter output, TextWriter error, Func<DateTime> clock)
    {
        if (path is null) return 2;
        var package = Load(path);
        var issues = new List<PackIssue>(package.Issues);
        var version = platform is not null ? SemVer.Parse(platform) : PlatformVersion.Current;
        issues.AddRange(PackReader.CheckCompatibility(package, new PackCompatibilityContext(version, PackKinds.Packable.ToHashSet(StringComparer.Ordinal))));
        if (package.Manifest is { } m) issues.AddRange(PackBuilder.CrossCheckSolutionPack(package, m));
        TrustResult? trust = null;
        if ((trustFile is not null || publicKey is not null) && package.Manifest is not null)
            trust = new PackTrustPolicy(Keys(trustFile, publicKey, keyId)).EvaluateAsync(package.Manifest, package.CanonicalManifest, package.SignatureFile,
                new TrustContext(flags.Contains("--production"), flags.Contains("--allow-unsigned-synthetic"), clock()), PackLimits.Default).GetAwaiter().GetResult();
        string? signatureKey = null;
        if (package.SignatureFile is { } sig && PackSignatureDocument.Parse(sig, PackLimits.Default).Signature is { } parsed) signatureKey = parsed.KeyId;
        var errors = issues.Count(i => i.IsError);
        var report = new Dictionary<string, object?>
        {
            ["tool"] = new { name = "ecp-pack", platformVersion = PlatformVersion.Current.ToString() },
            ["generatedUtc"] = clock().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["package"] = new
            {
                file = Path.GetFileName(path), sha256 = package.Sha256, contentDigest = package.ContentDigest, sizeBytes = package.Bytes.Length,
                packKey = package.Manifest?.PackKey, version = package.Manifest?.Version.ToString(), synthetic = package.Manifest?.Synthetic,
                contentFiles = package.Manifest?.Content.Select(c => new { c.Path, c.Kind, c.Sha256 }).ToList(),
            },
            ["validation"] = new
            {
                valid = errors == 0, errors, warnings = issues.Count(i => i.Severity == PackSeverity.Warning),
                findings = issues.Where(i => i.Severity != PackSeverity.Info).Select(i => new { code = i.Code, severity = i.Severity.ToString(), message = Safe(i.Message), entry = i.Entry, fix = i.Remediation is null ? null : Safe(i.Remediation) }).ToList(),
            },
            ["signature"] = new { present = package.SignatureFile is not null, keyId = signatureKey, evaluated = trust is not null, status = trust is null ? "Not evaluated (no trust file or public key was given)" : PackInspector.DescribeSignature(package, trust) },
            ["note"] = "This report describes the package. It is not an approval, and it contains no pack content, no key material and no signature bytes.",
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        if (outPath is null) output.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json + Environment.NewLine);
            output.WriteLine($"Report written to {Path.GetFileName(outPath)}: {(errors == 0 ? "valid" : errors + " error(s)")}.");
        }
        return errors == 0 ? 0 : 1;
    }

    private static int Hash(string? path, TextWriter output, TextWriter error)
    {
        if (path is null) return 2;
        var package = Load(path);
        output.WriteLine($"SHA-256:        {package.Sha256}");
        if (package.CanonicalManifest.Length > 0) output.WriteLine($"Content digest: {package.ContentDigest}");
        if (package.Manifest is { } m) foreach (var c in m.Content) output.WriteLine($"  {c.Sha256}  {c.Path}");
        return package.IsStructurallyValid ? 0 : 1;
    }

    private static IPackKeyResolver Keys(string? trustFile, string? publicKey, string? keyId)
    {
        var keys = new List<TrustedKey>();
        if (publicKey is not null && keyId is not null)
            keys.Add(new TrustedKey(keyId, SignatureAlgorithms.Es256, PackKeys.PublicKeyFromPem(File.ReadAllText(publicKey)), TrustedKeyStatus.Active, null, null, "command line"));
        if (trustFile is not null) keys.AddRange(TrustFile.Read(File.ReadAllText(trustFile)));
        return new StaticKeyResolver(keys);
    }

    private static int Inspect(string? path, string? trustFile, string? publicKey, string? keyId, HashSet<string> flags, TextWriter output, TextWriter error, Func<DateTime> clock)
    {
        if (path is null) return 2;
        var package = Load(path);
        TrustResult? trust = null;
        if ((trustFile is not null || publicKey is not null) && package.Manifest is { } m)
            trust = new PackTrustPolicy(Keys(trustFile, publicKey, keyId)).EvaluateAsync(m, package.CanonicalManifest, package.SignatureFile,
                new TrustContext(flags.Contains("--production"), flags.Contains("--allow-unsigned-synthetic"), clock()), PackLimits.Default).GetAwaiter().GetResult();
        output.Write(PackInspector.Summarise(package, trust));
        return package.IsStructurallyValid ? 0 : 1;
    }

    private static string ResolveKey(string? keyRef, string? keyFile, string? keyEnv, Func<string, string?> env)
    {
        var given = new[] { keyRef, keyFile is null ? null : "file:" + keyFile, keyEnv is null ? null : "env:" + keyEnv }.Where(x => x is not null).ToList();
        if (given.Count != 1) throw new KeyReferenceException("Give exactly one of --key-ref, --key-file or --key-env.", usage: true);
        var reference = given[0]!;
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = reference[4..];
            return string.IsNullOrWhiteSpace(name) || env(name) is not { Length: > 0 } value ? throw new KeyReferenceException($"The environment variable '{name}' is not set or is empty.") : value;
        }
        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = reference[5..];
            if (!File.Exists(path)) throw new KeyReferenceException($"The key file '{Path.GetFileName(path)}' was not found.");
            return File.ReadAllText(path);
        }
        throw new KeyReferenceException("This tool resolves env:NAME and file:PATH key references. Export the key to one of them where the signing is done; a vault reference is not resolved by the standalone tool.");
    }

    private static int Sign(string? path, string? outPath, string? keyRef, string? keyFile, string? keyEnv, string? keyId, TextWriter output, TextWriter error, Func<string, string?> env, Func<DateTime> clock)
    {
        if (path is null || outPath is null || keyId is null) return 2;
        string pem;
        try { pem = ResolveKey(keyRef, keyFile, keyEnv, env); }
        catch (KeyReferenceException ex) when (ex.IsUsage) { error.WriteLine(ex.Message); return 2; }
        var package = Load(path);
        if (!package.IsStructurallyValid) { error.WriteLine("The package is not valid, so it will not be signed:"); PrintIssues(package.Issues, error); return 1; }
        EcdsaP256Signer signer;
        try { signer = EcdsaP256Signer.FromPem(pem, keyId); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { throw new KeyReferenceException("The key is not a valid ECDSA P-256 private key in PEM form."); }
        using (signer)
        {
            var signed = PackBuilder.Sign(package, signer, clock());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllBytes(outPath, signed);
            output.WriteLine($"Signed {package.Manifest!.PackKey} {package.Manifest.Version} with key {keyId} ({SignatureAlgorithms.Es256}).");
            output.WriteLine($"SHA-256: {Convert.ToHexStringLower(SHA256.HashData(signed))}");
        }
        return 0;
    }

    private static int VerifyCommand(string? path, string? trustFile, string? publicKey, string? keyId, HashSet<string> flags, TextWriter output, TextWriter error, Func<DateTime> clock)
    {
        if (path is null) return 2;
        if (trustFile is null && publicKey is null) { error.WriteLine("Give --trust-file, or --public-key with --key-id."); return 2; }
        var package = Load(path);
        if (!package.IsStructurallyValid) { error.WriteLine("The package is not valid:"); PrintIssues(package.Issues, error); return 1; }
        var trust = new PackTrustPolicy(Keys(trustFile, publicKey, keyId)).EvaluateAsync(package.Manifest!, package.CanonicalManifest, package.SignatureFile,
            new TrustContext(flags.Contains("--production"), flags.Contains("--allow-unsigned-synthetic"), clock()), PackLimits.Default).GetAwaiter().GetResult();
        output.WriteLine($"Signature: {PackInspector.DescribeSignature(package, trust)}");
        if (!trust.IsAcceptable) { PrintIssues(trust.Issues, error); return 1; }
        PrintIssues(trust.Issues, output);
        return 0;
    }

    private static int Compare(string? a, string? b, TextWriter output, TextWriter error)
    {
        if (a is null || b is null) return 2;
        var pa = Load(a); var pb = Load(b);
        if (!pa.IsStructurallyValid || !pb.IsStructurallyValid) { error.WriteLine("One of the packages is not valid."); return 1; }
        var same = pa.ContentDigest == pb.ContentDigest && pa.Content.Count == pb.Content.Count && pa.Content.All(kv => pb.Content.TryGetValue(kv.Key, out var o) && kv.Value.AsSpan().SequenceEqual(o));
        var identicalUnsigned = same && PackBuilder.Unsigned(pa).AsSpan().SequenceEqual(PackBuilder.Unsigned(pb));
        output.WriteLine(identicalUnsigned ? "The packages are identical apart from any signature." : "The packages differ.");
        return identicalUnsigned ? 0 : 1;
    }

    private static int KeyGen(string? outPrivate, string? outPublic, HashSet<string> flags, TextWriter output, TextWriter error)
    {
        if (outPrivate is null || outPublic is null) return 2;
        if ((File.Exists(outPrivate) || File.Exists(outPublic)) && !flags.Contains("--force")) { error.WriteLine("A key file already exists; refusing to overwrite it (use --force to replace)."); return 1; }
        var (privatePem, spki) = PackKeys.Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPrivate))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPublic))!);
        File.WriteAllText(outPrivate, privatePem);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outPrivate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(outPublic, PackKeys.ToPublicPem(spki));
        output.WriteLine($"Wrote a new ECDSA P-256 key pair. Public key fingerprint: {PackKeys.Fingerprint(spki)}");
        output.WriteLine("Keep the private key file out of source control and out of the application's folders. The platform never needs it.");
        return 0;
    }
}

/// <summary>A trust file lists public keys: <c>{ "keys": [ { "keyId", "algorithm", "publicKeyPem", "notBeforeUtc", "notAfterUtc", "revoked", "label" } ] }</c>. Public keys only.</summary>
public static class TrustFile
{
    public static IReadOnlyList<TrustedKey> Read(string json, string source = "trust file")
    {
        using var doc = CanonicalJson.Parse(Encoding.UTF8.GetBytes(json));
        var keys = new List<TrustedKey>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array) throw new FormatException("The trust file must have a 'keys' list.");
        foreach (var k in list.EnumerateArray())
        {
            string? Text(string n) => k.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var keyId = Text("keyId"); var pem = Text("publicKeyPem");
            if (keyId is null || !PackSignatureDocument.KeyIdPattern().IsMatch(keyId) || pem is null) throw new FormatException("Each key needs a valid keyId and a publicKeyPem.");
            if (pem.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)) throw new FormatException("A trust file holds public keys only.");
            DateTime? Date(string n) => Text(n) is { } s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            var revoked = k.TryGetProperty("revoked", out var r) && r.ValueKind == JsonValueKind.True;
            keys.Add(new TrustedKey(keyId, Text("algorithm") ?? SignatureAlgorithms.Es256, PackKeys.PublicKeyFromPem(pem), revoked ? TrustedKeyStatus.Revoked : TrustedKeyStatus.Active, Date("notBeforeUtc"), Date("notAfterUtc"), source, Text("label")));
        }
        return keys;
    }
}

/// <summary>A key reference that could not be resolved or a key that could not be used. Carries no key material. <see cref="IsUsage"/> marks a mistake in how the command was written rather than in the environment.</summary>
public sealed class KeyReferenceException(string message, bool usage = false) : Exception(message)
{
    public bool IsUsage { get; } = usage;
}
