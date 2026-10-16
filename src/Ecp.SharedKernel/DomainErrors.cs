namespace Ecp.SharedKernel;

/// <summary>A business rule was violated. Carries a stable code and a plain-language message.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class Guard
{
    public static string NotBlank(string? value, string field, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("required", $"{field} is required.");
        var v = value.Trim();
        if (v.Length > maxLength)
            throw new DomainException("too_long", $"{field} must be at most {maxLength} characters (it is never truncated).");
        return v;
    }

    public static string? OptionalBounded(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Length > maxLength)
            throw new DomainException("too_long", $"{field} must be at most {maxLength} characters (it is never truncated).");
        return v;
    }

    public static Guid NotEmpty(Guid value, string field)
    {
        if (value == Guid.Empty) throw new DomainException("required", $"{field} is required.");
        return value;
    }

    public static DateTime Utc(DateTime value, string field)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new DomainException("not_utc", $"{field} must be a UTC instant.");
        return value;
    }
}

/// <summary>The caller is authenticated and the resource is in scope, but the action is not permitted (HTTP 403).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>The resource does not exist for this caller: absent, in another tenant, or outside the caller's scopes (HTTP 404, no existence oracle).</summary>
public sealed class ResourceNotFoundException(string message = "The resource was not found.") : Exception(message);

/// <summary>Ordered information-classification levels (lowest first). The default scale is a placeholder until the owner confirms the final set (Q-06); a tenant can configure its own.</summary>
public sealed class ClassificationScale
{
    public static readonly ClassificationScale Default = new(["Public", "Internal", "Confidential", "Restricted"], "Internal");

    private readonly string[] _levels;

    public ClassificationScale(IEnumerable<string> orderedLevels, string defaultLevel)
    {
        _levels = orderedLevels.Select(l => Guard.NotBlank(l, "Classification level", 50)).ToArray();
        if (_levels.Length < 1 || _levels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _levels.Length)
            throw new DomainException("invalid_classification_scale", "Classification levels must be a non-empty list of distinct names.");
        DefaultLevel = _levels.FirstOrDefault(l => l.Equals(defaultLevel, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("invalid_classification_scale", "The default classification must be one of the levels.");
    }

    public IReadOnlyList<string> Levels => _levels;
    public string DefaultLevel { get; }

    /// <summary>Position of a level (0 = lowest), or -1 when the level is not in the scale. Unknown levels never satisfy a ceiling.</summary>
    public int Rank(string? level) => level is null ? -1 : Array.FindIndex(_levels, l => l.Equals(level, StringComparison.OrdinalIgnoreCase));

    public bool IsKnown(string? level) => Rank(level) >= 0;

    /// <summary>True when a record of <paramref name="recordLevel"/> may be seen under <paramref name="ceiling"/>.</summary>
    public bool Permits(string? ceiling, string? recordLevel)
    {
        var c = Rank(ceiling); var r = Rank(recordLevel);
        return c >= 0 && r >= 0 && r <= c;
    }

    /// <summary>All levels at or below the ceiling (empty when the ceiling is unknown).</summary>
    public IReadOnlyList<string> AtOrBelow(string? ceiling)
    {
        var c = Rank(ceiling);
        return c < 0 ? [] : _levels.Take(c + 1).ToArray();
    }
}
