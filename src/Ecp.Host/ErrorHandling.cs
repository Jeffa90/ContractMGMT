using Ecp.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host;

/// <summary>Maps exceptions to RFC 9457 problem details with stable codes. Internals are never returned.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, title) = exception switch
        {
            DomainException d => (StatusCodes.Status422UnprocessableEntity, d.Code, d.Message),
            TenantScopeMissingException => (StatusCodes.Status403Forbidden, "tenant_scope_missing", "No tenant scope is established for this request."),
            TenantMismatchException => (StatusCodes.Status403Forbidden, "tenant_mismatch", "The operation targets a different tenant."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")
        };
        if (status >= 500) logger.LogError(exception, "Unhandled exception. CorrelationId={CorrelationId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = null });
    }
}
