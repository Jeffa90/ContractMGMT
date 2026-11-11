using System.Security.Cryptography.X509Certificates;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.DataProtection;

namespace Ecp.Host;

/// <summary>How the data-protection key ring (cookies, antiforgery, setup state) is stored and protected. The keys never leave the key directory in clear text unless the platform offers no key protection.</summary>
public enum KeyProtection
{
    /// <summary>Windows: protected to the local machine (DPAPI). Any process on that server can read them, so the folder permissions matter. The default on Windows because it does not depend on a loaded user profile.</summary>
    DpapiMachine,

    /// <summary>Windows: protected to the application-pool account (DPAPI current user). Needs that account's profile to be loaded; a different account cannot read the keys.</summary>
    DpapiUser,

    /// <summary>Windows: protected with a certificate from LocalMachine\My whose private key the application-pool account can read. Lets the keys move between servers.</summary>
    Certificate,

    /// <summary>No encryption at rest. Only the default off Windows (the container image, Linux): protect the volume and its permissions.</summary>
    None
}

public sealed record DataProtectionSettings(string KeyDirectory, KeyProtection Protection, string CertificateThumbprint)
{
    public static DataProtectionSettings From(IConfiguration config, string dataDirectory)
    {
        var configured = config["Ecp:DataProtection:KeyDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(dataDirectory, "keyring") : configured.Trim();
        var protection = Enum.TryParse<KeyProtection>(config["Ecp:DataProtection:Protection"], true, out var p) ? p
            : string.IsNullOrWhiteSpace(config["Ecp:DataProtection:Protection"]) ? (OperatingSystem.IsWindows() ? KeyProtection.DpapiMachine : KeyProtection.None)
            : (KeyProtection)(-1);   // an unrecognised value is refused by Validate, never silently replaced
        return new DataProtectionSettings(directory, protection, (config["Ecp:DataProtection:CertificateThumbprint"] ?? "").Trim());
    }

    /// <summary>Problems that make the configuration unusable. Evaluated for every mode; the key directory rules for Production are in the Production guard.</summary>
    public IReadOnlyList<GuardViolation> Validate()
    {
        var problems = new List<GuardViolation>();
        if (!Enum.IsDefined(Protection))
            problems.Add(new("R-KEY-01", "Ecp:DataProtection:Protection must be DpapiMachine, DpapiUser, Certificate or None."));
        else if (Protection != KeyProtection.None && !OperatingSystem.IsWindows())
            problems.Add(new("R-KEY-01", $"Key protection {Protection} is only available on Windows; use None and protect the key directory with file permissions."));
        else if (Protection == KeyProtection.Certificate && CertificateThumbprint.Length == 0)
            problems.Add(new("R-KEY-01", "Ecp:DataProtection:CertificateThumbprint is required when the key protection is Certificate."));
        return problems;
    }

    /// <summary>Applies persistence and protection. The application name is fixed so that every instance and every redeploy shares one ring.</summary>
    public IDataProtectionBuilder Apply(IDataProtectionBuilder builder)
    {
        builder.SetApplicationName("EnterpriseContractPlatform").PersistKeysToFileSystem(new DirectoryInfo(KeyDirectory));
        if (!OperatingSystem.IsWindows()) return builder;
        switch (Protection)
        {
            case KeyProtection.DpapiMachine: builder.ProtectKeysWithDpapi(protectToLocalMachine: true); break;
            case KeyProtection.DpapiUser: builder.ProtectKeysWithDpapi(); break;
            case KeyProtection.Certificate:
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, CertificateThumbprint, validOnly: false);
                    if (found.Count == 0) throw new InvalidOperationException("The data-protection certificate was not found in LocalMachine\\My (thumbprint " + CertificateThumbprint + ").");
                    builder.ProtectKeysWithCertificate(found[0]);
                }
                break;
        }
        return builder;
    }

    public string DescribeProtection() => Protection switch
    {
        KeyProtection.DpapiMachine => "keys are encrypted to this machine (Windows DPAPI, machine scope)",
        KeyProtection.DpapiUser => "keys are encrypted to the application-pool account (Windows DPAPI, current user; its profile must be loaded)",
        KeyProtection.Certificate => "keys are encrypted with a certificate from LocalMachine\\My",
        _ => "keys are NOT encrypted at rest; the key directory's permissions are the only protection"
    };
}

/// <summary>
/// R-SEC-04: the key ring is where it should be, can be written, holds a usable key, and is described honestly. A ring that is missing or unwritable means
/// every user is signed out after a restart and in-progress forms fail; no stored data is lost.
/// </summary>
public sealed class DataProtectionReadinessCheck(DataProtectionSettings settings, StartupFacts facts, IKeyRingInspector ring, TimeProvider time) : IReadinessCheck
{
    public string Id => "R-SEC-04";
    private const string Name = "Data-protection key ring";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var issues = new List<string>();
        if (!Directory.Exists(settings.KeyDirectory)) issues.Add("the key directory does not exist");
        else
        {
            try
            {
                var probe = Path.Combine(settings.KeyDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x"); File.Delete(probe);
            }
            catch (Exception) { issues.Add("the application cannot write to the key directory"); }
        }
        var state = ring.Inspect(time.GetUtcNow().UtcDateTime);
        if (!state.Readable) issues.Add("the key ring could not be read");
        if (facts.Mode == EcpMode.Production && settings.Protection == KeyProtection.None && OperatingSystem.IsWindows())
            issues.Add("keys are not encrypted at rest on Windows");
        var where = $"Key ring at {settings.KeyDirectory}; {settings.DescribeProtection()}; {state.UsableKeys} usable key(s).";
        return Task.FromResult(issues.Count == 0
            ? new ReadinessResult(Id, Name, ReadinessStatus.Healthy, where)
            : new ReadinessResult(Id, Name, facts.Mode == EcpMode.Production ? ReadinessStatus.Unhealthy : ReadinessStatus.Degraded, where + " Attention: " + string.Join("; ", issues) + "."));
    }
}
