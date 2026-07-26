namespace CogniLens.Core.Dtos;

/// <summary>
/// The analyze job queue payload.
/// </summary>
/// <param name="CallId">The call to transcribe and analyse.</param>
/// <param name="TraceParent">
/// W3C <c>traceparent</c> of the request that enqueued this job, carried across the queue hop so
/// the Worker's processing spans join the Api's trace instead of starting a disconnected one.
/// Nullable and defaulted so messages written before this field existed still deserialise, and so
/// a message enqueued with no ambient trace is still valid.
/// </param>
/// <param name="TraceState">
/// W3C <c>tracestate</c>. Unused by CogniLens itself, but dropping it would silently break vendor
/// trace context for anything upstream, and carrying it costs one nullable string.
/// </param>
public record AnalyzeJobMessage(Guid CallId, string? TraceParent = null, string? TraceState = null);
