using System.Diagnostics.Metrics;

namespace CogniLens.Infrastructure.Observability;

/// <summary>
/// The custom instruments CogniLens emits. Registered with OpenTelemetry by meter *name*
/// (<see cref="CogniLensTelemetry.MeterName"/>) in <see cref="ObservabilityExtensions"/>.
/// </summary>
/// <remarks>
/// Every tag used here is deliberately low-cardinality. Application Insights bills custom metrics
/// per time-series, so a tag carrying a call id or a message id would turn one metric into one
/// series per job — the kind of mistake that only shows up on the invoice. Per-job identifiers
/// belong on spans, which is where they are.
/// </remarks>
public static class CogniLensMetrics
{
    private static readonly Meter Meter = new(CogniLensTelemetry.MeterName);

    /// <summary>Total tokens billed, split by direction. This is the cost meter.</summary>
    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>(
        "cognilens.llm.tokens",
        unit: "{token}",
        description: "Azure OpenAI tokens consumed, tagged by direction (input/output) and deployment.");

    /// <summary>
    /// Tokens consumed by a single job. A counter answers "what is this costing per month"; only
    /// a distribution answers "is one pathological call burning the budget", which is the question
    /// that actually drives a fix.
    /// </summary>
    public static readonly Histogram<long> LlmTokensPerJob = Meter.CreateHistogram<long>(
        "cognilens.llm.tokens_per_job",
        unit: "{token}",
        description: "Total Azure OpenAI tokens consumed by one QA analysis, including any schema retry.");

    /// <summary>
    /// Structured-output responses that failed validation. Non-zero here means paying twice for
    /// one analysis; sustained non-zero on the retry attempt means jobs are failing outright.
    /// </summary>
    public static readonly Counter<long> LlmSchemaFailures = Meter.CreateCounter<long>(
        "cognilens.llm.schema_failures",
        unit: "{failure}",
        description: "QA analysis responses that failed schema validation, tagged by attempt.");

    /// <summary>Wall-clock time spent in batch transcription, including the polling wait.</summary>
    public static readonly Histogram<double> TranscriptionDuration = Meter.CreateHistogram<double>(
        "cognilens.transcription.duration",
        unit: "s",
        description: "Wall-clock seconds spent transcribing one call, including job submission and polling.");

    /// <summary>
    /// Length of the audio itself, which is what Speech actually bills on. Kept separate from
    /// <see cref="TranscriptionDuration"/> because the two diverge badly — a 3-minute call can
    /// sit in the batch queue for far longer than it takes to transcribe, and conflating them
    /// would make cost look like latency and vice versa.
    /// </summary>
    public static readonly Histogram<double> TranscriptionAudioDuration = Meter.CreateHistogram<double>(
        "cognilens.transcription.audio_duration",
        unit: "s",
        description: "Seconds of audio transcribed, derived from the last recognised segment's end time.");

    /// <summary>
    /// End-to-end Worker processing time for one message, tagged by outcome. Note this starts at
    /// dequeue, not at upload — it excludes queue wait, which <see cref="QueueDepth"/> covers.
    /// </summary>
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "cognilens.job.duration",
        unit: "s",
        description: "Seconds to process one analyze job from dequeue to completion, tagged by outcome.");

    /// <summary>
    /// Approximate queue backlog. Observed rather than incremented because the authoritative count
    /// lives in Azure Storage, not in this process — several replicas consume the same queue, so a
    /// locally-maintained counter would be wrong on every replica.
    /// </summary>
    /// <remarks>
    /// The gauge only reports while a Worker replica is awake. With <c>minReplicas: 0</c> that
    /// means an idle system shows no data rather than a flat zero — correct, but worth knowing
    /// before reading a gap in the chart as an outage.
    /// </remarks>
    public static ObservableGauge<long> CreateQueueDepthGauge(Func<IEnumerable<Measurement<long>>> observe) =>
        Meter.CreateObservableGauge(
            "cognilens.queue.depth",
            observe,
            unit: "{message}",
            description: "Approximate number of messages waiting in the analyze job queue.");
}
