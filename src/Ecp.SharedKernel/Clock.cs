namespace Ecp.SharedKernel;

/// <summary>Source of the current UTC instant. All timestamps are stored in UTC.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
