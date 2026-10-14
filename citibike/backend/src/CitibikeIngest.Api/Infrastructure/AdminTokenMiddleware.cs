using System.Security.Cryptography;
using System.Text;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>
/// The API sits behind a public subdomain, so starting a run (a multi-GB download, or a write to the weather
/// table) needs a shared secret: X-Admin-Token on POST /api/ingest/*, /api/weather/* and /api/calendar/*. Reads are open. Without a configured token the
/// check is skipped (local dev); Program.cs logs a warning in that case.
/// </summary>
public sealed class AdminTokenMiddleware(RequestDelegate next, IOptions<IngestOptions> options)
{
    public const string HeaderName = "X-Admin-Token";

    private readonly byte[]? expected = string.IsNullOrEmpty(options.Value.AdminToken)
        ? null
        : Encoding.UTF8.GetBytes(options.Value.AdminToken);

    public async Task InvokeAsync(HttpContext context)
    {
        if (expected is not null && RequiresToken(context.Request))
        {
            var given = Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync($"Missing or invalid {HeaderName} header");
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresToken(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (request.Path.StartsWithSegments("/api/ingest")
            || request.Path.StartsWithSegments("/api/weather")
            || request.Path.StartsWithSegments("/api/calendar"));
}
