using System.Diagnostics;

namespace CogniLens.Infrastructure.Observability;

/// <summary>
/// The single <see cref="ActivitySource"/> both services emit custom spans from.
/// </summary>
/// <remarks>
/// It lives in Infrastructure rather than in either host because the trace has to survive the
/// queue hop: the Api starts the producer span here and the Worker starts the consumer span from
/// the same source, and an <see cref="ActivitySource"/> only produces spans if its *name* was
/// registered with <c>AddSource</c>. Two separately-declared sources would mean two names to keep
/// in sync across two projects, and forgetting one silently drops half the trace rather than
/// failing the build.
/// </remarks>
public static class CogniLensTelemetry
{
    public const string ActivitySourceName = "CogniLens";
    public const string MeterName = "CogniLens";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
