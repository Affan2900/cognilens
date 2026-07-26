using System.Diagnostics;

namespace CogniLens.Api;

/// <summary>
/// Surfaces the current trace id to the caller as <c>X-Correlation-Id</c>, and records a caller's
/// own correlation id on the span when one is supplied.
/// </summary>
/// <remarks>
/// Deliberately does not mint a second id. The W3C trace id already *is* the correlation id — it
/// is what ties the Api request, the queue hop and every Worker span together, and inventing a
/// parallel scheme would mean two ids that can disagree. All that was actually missing is a way
/// for someone reporting a problem to quote the id, which is what the response header provides.
/// </remarks>
public class TraceCorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    // A caller-supplied header is untrusted input that ends up in a queryable telemetry field,
    // so it is length-capped rather than passed through verbatim.
    private const int MaxClientCorrelationIdLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;

        if (context.Request.Headers.TryGetValue(HeaderName, out var inbound) && inbound.Count > 0)
        {
            var clientId = inbound.ToString();
            if (clientId.Length > MaxClientCorrelationIdLength)
            {
                clientId = clientId[..MaxClientCorrelationIdLength];
            }

            // Recorded alongside the trace id, never in place of it: an id chosen by the caller
            // cannot be trusted to be unique, and overwriting the trace id with it would corrupt
            // correlation for every other request that happened to pick the same value.
            activity?.SetTag("cognilens.client_correlation_id", clientId);
        }

        // TraceIdentifier is the fallback for the case where nothing is listening to the
        // ActivitySource — the header should still carry *something* greppable in the container log.
        var correlationId = activity?.TraceId.ToString() ?? context.TraceIdentifier;

        // Response headers are locked once the body starts, and any middleware downstream may
        // start it. OnStarting is the only hook that reliably lands before that happens.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }
}
