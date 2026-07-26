using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Queues;
using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
using CogniLens.Infrastructure.Observability;
using CogniLens.Infrastructure.Resilience;
using CogniLens.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure.Queues;

public class AzureQueueJobQueue(QueueServiceClient queueServiceClient, IOptions<StorageOptions> options)
    : IJobQueue
{
    private readonly StorageOptions _options = options.Value;

    public async Task<string> EnqueueAnalyzeJobAsync(Guid callId, CancellationToken cancellationToken)
    {
        var queueClient = queueServiceClient.GetQueueClient(_options.QueueName);

        // Span name and attributes follow the OpenTelemetry messaging conventions so App Insights
        // classifies this as a queue dependency rather than an unlabelled custom span.
        using var activity = CogniLensTelemetry.ActivitySource.StartActivity(
            $"{_options.QueueName} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "azure_queue_storage");
        activity?.SetTag("messaging.operation", "publish");
        activity?.SetTag("messaging.destination.name", _options.QueueName);
        activity?.SetTag("cognilens.call_id", callId);

        // The traceparent written into the message has to be *this* span's id so the Worker's
        // consumer span becomes its child. Falling back to Activity.Current covers the case where
        // nothing sampled the ActivitySource above (StartActivity returns null then) but the
        // ambient ASP.NET Core request activity still exists — losing the hop merely because
        // tracing is partially configured would be the worst of both worlds.
        //
        // Activity.Id *is* the W3C traceparent string: .NET defaults to ActivityIdFormat.W3C, so
        // this is the header value verbatim, not a legacy hierarchical id.
        var traceCarrier = activity ?? Activity.Current;

        return await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            await queueClient.CreateIfNotExistsAsync(cancellationToken: ct);

            var body = JsonSerializer.Serialize(new AnalyzeJobMessage(
                callId,
                TraceParent: traceCarrier?.Id,
                TraceState: traceCarrier?.TraceStateString));
            var receipt = await queueClient.SendMessageAsync(body, cancellationToken: ct);

            activity?.SetTag("messaging.message.id", receipt.Value.MessageId);
            return receipt.Value.MessageId;
        }, cancellationToken);
    }
}
