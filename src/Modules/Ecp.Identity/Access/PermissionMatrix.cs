using System.Reflection;
using Ecp.Identity.Domain;

namespace Ecp.Identity.Access;

/// <summary>How a role holds a permission: not at all, outright within its scopes (Y), or subject to a relationship rule (S).</summary>
public enum Grant { None, Yes, Scoped }

public sealed record PermissionInfo(string Code, string Description, string Sensitivity);

/// <summary>
/// The confirmed permission catalogue and role-permission matrix, loaded from the repository's own CSV files (embedded; one canonical
/// copy). The evaluator uses the matrix as data: there are no role-specific branches for what a role may do.
/// </summary>
public sealed class PermissionMatrix
{
    private readonly Dictionary<string, Dictionary<string, Grant>> _matrix;
    private readonly Dictionary<string, PermissionInfo> _permissions;

    private PermissionMatrix(Dictionary<string, Dictionary<string, Grant>> matrix, Dictionary<string, PermissionInfo> permissions)
    { _matrix = matrix; _permissions = permissions; }

    public static PermissionMatrix Instance { get; } = Load();

    public IReadOnlyCollection<string> Permissions => _permissions.Keys;
    public bool IsKnown(string permission) => _permissions.ContainsKey(permission);
    public PermissionInfo Info(string permission) => _permissions[permission];

    public Grant Of(string role, string permission)
        => _matrix.TryGetValue(permission, out var row) && row.TryGetValue(role, out var grant) ? grant : Grant.None;

    /// <summary>Permissions the role holds at all (Y or S).</summary>
    public IEnumerable<string> PermissionsOf(string role) => _matrix.Where(kv => kv.Value.TryGetValue(role, out var g) && g != Grant.None).Select(kv => kv.Key);

    private static PermissionMatrix Load()
    {
        var assembly = typeof(PermissionMatrix).Assembly;
        var catalogue = ReadCsv(assembly, "Ecp.Identity.permission-catalogue.csv");
        var permissions = catalogue.Skip(1).ToDictionary(r => r[0], r => new PermissionInfo(r[0], r[1], r[3]));
        var rows = ReadCsv(assembly, "Ecp.Identity.role-permission-matrix.csv");
        var header = rows[0];
        var matrix = new Dictionary<string, Dictionary<string, Grant>>();
        foreach (var row in rows.Skip(1))
        {
            var perRole = new Dictionary<string, Grant>();
            for (var i = 1; i < header.Length - 1; i++) // last column is the basis note
            {
                var cell = i < row.Length ? row[i].Trim() : "";
                perRole[header[i]] = cell switch { "Y" => Grant.Yes, "S" => Grant.Scoped, "" => Grant.None, _ => throw new InvalidDataException($"Unexpected matrix cell '{cell}' for {row[0]}/{header[i]}") };
            }
            matrix[row[0]] = perRole;
        }
        var missing = RoleCodes.All.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0) throw new InvalidDataException("The role matrix lacks columns for: " + string.Join(", ", missing));
        var undeclared = matrix.Keys.Where(k => !permissions.ContainsKey(k)).ToList();
        if (undeclared.Count > 0) throw new InvalidDataException("Matrix permissions missing from the catalogue: " + string.Join(", ", undeclared));
        return new PermissionMatrix(matrix, permissions);
    }

    private static List<string[]> ReadCsv(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Embedded resource {resource} not found.");
        using var reader = new StreamReader(stream);
        var rows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            rows.Add(Split(line.TrimStart('﻿')));
        }
        return rows;
    }

    private static string[] Split(string line)
    {
        var fields = new List<string>(); var sb = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') quoted = false; else sb.Append(c); }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
