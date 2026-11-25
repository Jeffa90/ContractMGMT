namespace JbsContracts.Api.Infrastructure;

/// <summary>
/// Settings, read once at start-up. Each value is read from its JBS Contracts name first ("Contracts:..."), then from the
/// name the old app used ("Ecp:..."), so the variables already on the GitHub QA environment work without changes
/// (decision D-36). Nothing here is a secret: SQL Server uses Windows integrated authentication.
/// </summary>
public sealed class AppSettings
{
    public required string ConnectionString { get; init; }
    public string? DataDirectory { get; init; }
    public string? LogDirectory { get; init; }
    public string LogFileName { get; init; } = "log-.txt";
    public int LogRetainedFiles { get; init; } = 31;
    public IReadOnlyList<FirstAdmin> FirstAdmins { get; init; } = [];
    /// <summary>"Windows" (IIS Windows sign-in, the only mode outside Development and Testing) or "Header" (local development and tests).</summary>
    public string AuthMode { get; init; } = "Windows";
    /// <summary>Apply database/*.sql at start-up. Local development and tests only; on the servers the deploy applies them.</summary>
    public bool ApplyDatabaseScripts { get; init; }

    public string? FontsDirectory => string.IsNullOrWhiteSpace(DataDirectory) ? null : Path.Combine(DataDirectory, "fonts");
    public string? DocumentsDirectory => string.IsNullOrWhiteSpace(DataDirectory) ? null : Path.Combine(DataDirectory, "documents");

    /// <summary>The domain typed in front of a logon when an admin types just the user name, e.g. AMH. Taken from the first admins.</summary>
    public string? DefaultDomain => FirstAdmins
        .Select(a => WindowsAccount.TryNormalise(a.Account, null, out var account) ? account.Split('\\')[0] : null)
        .FirstOrDefault(d => d is not null);

    public static AppSettings From(IConfiguration config, IHostEnvironment env)
    {
        string? Read(string name, string? legacy = null)
        {
            var value = config[$"Contracts:{name}"];
            if (string.IsNullOrWhiteSpace(value) && legacy is not null) value = config[legacy];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var connectionString = config["Database:ConnectionString"] ?? config.GetConnectionString("Contracts");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No database connection string. Set Database:ConnectionString (APPENV_Database__ConnectionString on the GitHub environment).");

        var admins = new List<FirstAdmin>();
        foreach (var section in new[] { "Contracts:FirstAdmins", "Ecp:Security:FailsafeAdmins" })
        {
            foreach (var child in config.GetSection(section).GetChildren())
            {
                var account = child["Account"];
                if (string.IsNullOrWhiteSpace(account)) continue;
                admins.Add(new FirstAdmin(account.Trim(), (child["DisplayName"] ?? child["Name"] ?? account).Trim(), (child["Email"] ?? "").Trim()));
            }
        }

        var authMode = Read("AuthMode") ?? "Windows";
        var localOnly = env.IsDevelopment() || env.IsEnvironment("Testing");
        if (!string.Equals(authMode, "Windows", StringComparison.OrdinalIgnoreCase) && !localOnly)
            throw new InvalidOperationException($"Contracts:AuthMode '{authMode}' is only allowed in Development and Testing. Servers always use Windows sign-in.");
        var applyScripts = string.Equals(Read("ApplyDatabaseScripts"), "true", StringComparison.OrdinalIgnoreCase);
        if (applyScripts && !localOnly)
            throw new InvalidOperationException("Contracts:ApplyDatabaseScripts is only allowed in Development and Testing. On the servers the deploy applies the database scripts.");

        return new AppSettings
        {
            ConnectionString = connectionString,
            DataDirectory = Read("DataDirectory", "Ecp:DataDirectory"),
            LogDirectory = Read("Logging:Directory", "Ecp:Logging:Directory"),
            LogFileName = Read("Logging:FileName", "Ecp:Logging:FileName") ?? "log-.txt",
            LogRetainedFiles = int.TryParse(Read("Logging:RetainedFiles", "Ecp:Logging:RetainedFiles"), out var n) && n > 0 ? n : 31,
            FirstAdmins = admins,
            AuthMode = authMode,
            ApplyDatabaseScripts = applyScripts,
        };
    }
}

public sealed record FirstAdmin(string Account, string Name, string Email);
