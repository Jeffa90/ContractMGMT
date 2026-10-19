using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Ecp.Api.Tests;

/// <summary>Test servers have no remote address. This lets a test say which address a caller connects from (header <c>X-Test-Remote</c>), for example to play a reverse proxy, or to keep one caller's rate-limit budget apart from another's. Only in tests.</summary>
internal sealed class TestRemoteAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, n) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Remote", out var ip) && IPAddress.TryParse(ip.ToString(), out var address)) context.Connection.RemoteIpAddress = address;
            return n(context);
        });
        next(app);
    };
}
