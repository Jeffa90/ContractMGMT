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
