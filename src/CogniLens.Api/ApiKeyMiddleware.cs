namespace CogniLens.Api;

// Minimal shared-secret auth for the public API surface — required from the first Phase 3 deploy,
// per the non-negotiable that /analyze and /api/search can't go publicly reachable unauthenticated.
// Keys come from the ApiKeys config value (comma-separated), sourced from a Container Apps secret
// backed by no persisted plaintext — never a literal in code/config committed to source control.
public class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        var validKeys = (configuration["ApiKeys"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var provided = context.Request.Headers.TryGetValue(HeaderName, out var value) ? value.ToString() : null;

        if (validKeys.Length == 0 || provided is null || !validKeys.Contains(provided, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid API key.");
            return;
        }

        await next(context);
    }
}
