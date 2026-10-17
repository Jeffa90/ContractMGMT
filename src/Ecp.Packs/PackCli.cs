using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ecp.Packs;

/// <summary>
/// The command-line surface of the pack tool (<c>ecp-pack</c>). It lives in the library so the same code is run by the tool and by the tests. Output is sanitised: no key material, no signature bytes, no
/// server paths beyond the ones the operator typed. Exit codes: 0 success, 1 the package or request is not acceptable, 2 usage error.
/// </summary>
public static class PackCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment = null, Func<DateTime>? clock = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        clock ??= () => DateTime.UtcNow;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { output.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
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
                case "build": return Build(Need("--source"), Need("--out"), output, error);
                case "build-all": return BuildAll(Need("--source"), Need("--out"), output, error);
                case "validate": return Validate(Need("--package"), Opt("--platform-version"), output, error);
                case "hash": return Hash(Need("--package"), output, error);
                case "inspect": return Inspect(Need("--package"), Opt("--trust-file"), Opt("--public-key"), Opt("--key-id"), flags, output, error, clock);
                case "sign": return Sign(Need("--package"), Need("--out"), Opt("--key-file"), Opt("--key-env"), Need("--key-id"), output, error, environment, clock);
                case "verify": return VerifyCommand(Need("--package"), Opt("--trust-file"), Opt("--public-key"), Opt("--key-id"), flags, output, error, clock);
                case "compare": return Compare(Need("--a"), Need("--b"), output, error);
                case "keygen": return KeyGen(Need("--out-private"), Need("--out-public"), flags, output, error);
                default: error.WriteLine($"Unknown command '{command}'."); error.WriteLine(Usage); return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException or ArgumentException or JsonException)
        {
            error.WriteLine("The command failed: " + Safe(ex.Message));
            return 1;
        }
    }

    public const string Usage = """
        ecp-pack: build, inspect, sign and verify solution packs (.ecpkg)

          build     --source <pack-source-dir> --out <file.ecpkg>
          build-all --source <dir-of-pack-sources> --out <output-dir>
          validate  --package <file> [--platform-version x.y.z]
          hash      --package <file>
          inspect   --package <file> [--trust-file keys.json | --public-key key.pem --key-id id] [--production] [--allow-unsigned-synthetic]
          sign      --package <file> --out <file> --key-id <id> (--key-file <private.pem> | --key-env <VARIABLE>)
          verify    --package <file> (--trust-file keys.json | --public-key key.pem --key-id id) [--production] [--allow-unsigned-synthetic]
          compare   --a <file> --b <file>      (reproducibility: equal when everything except the signature is identical)
          keygen    --out-private <file> --out-public <file>      (writes a NEW key pair; keep the private key out of source control)

        A private key is read from a file or an environment variable that you supply; it is never printed or stored by this tool.
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

    private static int Sign(string? path, string? outPath, string? keyFile, string? keyEnv, string? keyId, TextWriter output, TextWriter error, Func<string, string?> env, Func<DateTime> clock)
    {
        if (path is null || outPath is null || keyId is null) return 2;
        if ((keyFile is null) == (keyEnv is null)) { error.WriteLine("Give exactly one of --key-file or --key-env."); return 2; }
        var pem = keyFile is not null ? File.ReadAllText(keyFile) : env(keyEnv!) ?? throw new ArgumentException($"The environment variable '{keyEnv}' is not set.");
        var package = Load(path);
        if (!package.IsStructurallyValid) { error.WriteLine("The package is not valid, so it will not be signed:"); PrintIssues(package.Issues, error); return 1; }
        using var signer = EcdsaP256Signer.FromPem(pem, keyId);
        var signed = PackBuilder.Sign(package, signer, clock());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, signed);
        output.WriteLine($"Signed {package.Manifest!.PackKey} {package.Manifest.Version} with key {keyId} ({SignatureAlgorithms.Es256}).");
        output.WriteLine($"SHA-256: {Convert.ToHexStringLower(SHA256.HashData(signed))}");
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
