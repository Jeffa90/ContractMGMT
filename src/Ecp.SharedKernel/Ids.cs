namespace Ecp.SharedKernel;

/// <summary>Identifier generation. Internal entities use immutable GUIDs (decision D3).</summary>
public static class Ids
{
    /// <summary>Time-ordered (version 7) GUID generated in the application.</summary>
    public static Guid New() => Guid.CreateVersion7();
}
