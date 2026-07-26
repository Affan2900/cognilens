using System.Diagnostics;
using System.Text.Json;
using CogniLens.Core.Dtos;
using CogniLens.Infrastructure.Observability;

namespace CogniLens.UnitTests;

/// <summary>
/// Guards the queue payload's trace-context contract. Every failure mode covered here is silent:
/// the pipeline keeps working and the only symptom is a trace that quietly splits in two, which
/// nothing in CI or in a smoke test would notice.
/// </summary>
public class AnalyzeJobTraceContextTests
{
    private const string SampleTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void Deserialises_a_payload_written_before_trace_fields_existed()
    {
        // Messages enqueued by the previous revision are still sitting in the queue when the new
        // Worker starts draining it. If this threw, the deploy would dead-letter every in-flight
        // job after three redeliveries.
        var message = JsonSerializer.Deserialize<AnalyzeJobMessage>(
            """{"CallId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""");

        Assert.NotNull(message);
        Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), message.CallId);
        Assert.Null(message.TraceParent);
        Assert.Null(message.TraceState);
    }

    [Fact]
    public void Payload_with_trace_fields_is_readable_by_a_consumer_that_predates_them()
    {
        // The other half of the canary problem: during the 90/10 traffic split the *old* Worker
        // revision is still running and will receive messages the new Api produced. This asserts
        // System.Text.Json's ignore-unknown-members default actually holds for this payload.
        var body = JsonSerializer.Serialize(new AnalyzeJobMessage(Guid.NewGuid(), SampleTraceParent, "vendor=x"));

        var asSeenByOldCode = JsonSerializer.Deserialize<CallIdOnly>(body);

        Assert.NotNull(asSeenByOldCode);
        Assert.NotEqual(Guid.Empty, asSeenByOldCode.CallId);
    }

    [Fact]
    public void Producer_activity_id_is_a_w3c_traceparent_that_reparents_the_consumer()
    {
        // The producer writes Activity.Id straight into the message on the assumption that .NET
        // formats it as a W3C traceparent, and the consumer feeds it back in as a parent id. If
        // either assumption breaks, the Worker's spans start a brand-new trace instead of joining
        // the Api's — the exact outcome Phase 5 exists to prevent.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CogniLensTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        string? carriedTraceParent;
        ActivityTraceId producerTraceId;
        ActivitySpanId producerSpanId;

        using (var producer = CogniLensTelemetry.ActivitySource.StartActivity("test publish", ActivityKind.Producer))
        {
            Assert.NotNull(producer);
            Assert.Equal(ActivityIdFormat.W3C, producer.IdFormat);
            carriedTraceParent = producer.Id;
            producerTraceId = producer.TraceId;
            producerSpanId = producer.SpanId;
        }

        Assert.NotNull(carriedTraceParent);

        using var consumer = CogniLensTelemetry.ActivitySource.StartActivity(
            "test process", ActivityKind.Consumer, carriedTraceParent);

        Assert.NotNull(consumer);
        Assert.Equal(producerTraceId, consumer.TraceId);
        Assert.Equal(producerSpanId, consumer.ParentSpanId);
    }

    private record CallIdOnly(Guid CallId);
}
