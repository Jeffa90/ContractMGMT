using Ecp.SharedKernel.Secrets;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace Ecp.Host.Logging;

/// <summary>Writes each event as one compact JSON line (rendered message included), then removes secret values from the finished line before it reaches the file or the console.</summary>
public sealed class RedactingJsonFormatter : ITextFormatter
{
    private readonly RenderedCompactJsonFormatter _inner = new();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(LogRedactor.Redact(buffer.ToString()));
    }
}
