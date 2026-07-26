using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using CogniLens.Core.Chunking;
using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
using CogniLens.Core.Entities;
using CogniLens.Core.Enums;
using CogniLens.Infrastructure.Observability;
using CogniLens.Infrastructure.Persistence;
using CogniLens.Infrastructure.Resilience;
using CogniLens.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CogniLens.Worker;

public class AnalyzeJobConsumer(
    QueueServiceClient queueServiceClient,
    IOptions<StorageOptions> storageOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<AnalyzeJobConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;

    // One message per receive, not five. Storage Queues makes *every* message in a batch invisible
    // for the same window starting at receive time, but this loop processes them one after another
    // — so messages 2..5 burn their entire lease waiting their turn, become visible again while
    // still being held, get redelivered to another replica, and dead-letter on DequeueCount alone
    // without anything having gone wrong. Batching only pays off when processing is fast; a job
    // here is dominated by a multi-minute batch transcription.
    private const int MaxMessagesPerReceive = 1;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // Must exceed the worst-case job, which is bounded by AzureSpeechTranscriptionService's
    // 15-minute polling timeout plus embedding, analysis and indexing. The 30 seconds this used to
    // be was shorter than a *successful* transcription, so the lease expired mid-job every time.
    //
    // The alternative is to renew the lease periodically with UpdateMessageAsync, which tracks the
    // real work more precisely and would return a message faster after a crash. It also means
    // threading a mutating pop receipt through every delete path. The cost of the simpler choice
    // is bounded and known: if a replica dies mid-job, that message waits 20 minutes before
    // anyone retries it. That is a worse failure mode than renewal, and a far better one than
    // guaranteed spurious redelivery.
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan QueueDepthRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly StorageOptions _options = storageOptions.Value;

    // Written by the poll loop, read by the metrics collection thread — hence the interlocked
    // access rather than a plain field.
    private long _approximateQueueDepth = -1;
    private ObservableGauge<long>? _queueDepthGauge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueClient = queueServiceClient.GetQueueClient(_options.QueueName);
        var poisonQueueClient = queueServiceClient.GetQueueClient(_options.PoisonQueueName);

        // The callback deliberately only reads a cached value. OpenTelemetry invokes observable
        // callbacks on its own collection thread with no cancellation token, so blocking it on a
        // storage round-trip would stall the export of every other metric alongside it.
        _queueDepthGauge = CogniLensMetrics.CreateQueueDepthGauge(ObserveQueueDepth);
        var lastQueueDepthRefresh = DateTimeOffset.MinValue;

        await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            await queueClient.CreateIfNotExistsAsync(cancellationToken: ct);
            await poisonQueueClient.CreateIfNotExistsAsync(cancellationToken: ct);
        }, stoppingToken);

        using (var scope = scopeFactory.CreateScope())
        {
            var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();
            await searchIndexService.EnsureIndexExistsAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Throttled to once every 15s rather than once per 2s poll: GetProperties is a billed
            // storage transaction, and backlog is a trend, not something that needs sub-second
            // resolution. ReceiveMessagesAsync itself carries no depth information, so this is a
            // separate call or nothing.
            if (DateTimeOffset.UtcNow - lastQueueDepthRefresh > QueueDepthRefreshInterval)
            {
                lastQueueDepthRefresh = DateTimeOffset.UtcNow;
                await RefreshQueueDepthAsync(queueClient, stoppingToken);
            }

            QueueMessage[] messages;
            try
            {
                messages = await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
                {
                    var response = await queueClient.ReceiveMessagesAsync(maxMessages: MaxMessagesPerReceive, visibilityTimeout: VisibilityTimeout, cancellationToken: ct);
                    return response.Value;
                }, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (messages.Length == 0)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            foreach (var message in messages)
            {
                await ProcessMessageAsync(queueClient, poisonQueueClient, message, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Owns the per-message span and duration metric; the actual work is in
    /// <see cref="ProcessMessageCoreAsync"/>, which returns the outcome both of them are tagged
    /// with. Split this way so every exit path — dead-letter, discard, duplicate, success,
    /// failure — is measured, rather than only the ones someone remembered to instrument.
    /// </summary>
    private async Task ProcessMessageAsync(QueueClient queueClient, QueueClient poisonQueueClient, QueueMessage message, CancellationToken stoppingToken)
    {
        using var activity = StartConsumerActivity(message);

        // Attached to every log line the pipeline emits below, including the ones written deep in
        // the AI services. Trace id already correlates them, but the message id is what someone
        // has in hand when a job is stuck in the queue and there is no trace to start from.
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.MessageId,
            ["DequeueCount"] = message.DequeueCount
        });

        var stopwatch = Stopwatch.StartNew();

        var outcome = "failed";
        try
        {
            outcome = await ProcessMessageCoreAsync(activity, queueClient, poisonQueueClient, message, stoppingToken);
        }
        finally
        {
            activity?.SetTag("cognilens.outcome", outcome);
            CogniLensMetrics.JobDuration.Record(
                stopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("outcome", outcome));

            logger.LogInformation(
                "Analyze job {MessageId} finished with outcome {Outcome} in {DurationSeconds:F1}s.",
                message.MessageId, outcome, stopwatch.Elapsed.TotalSeconds);
        }
    }

    private async Task<string> ProcessMessageCoreAsync(Activity? activity, QueueClient queueClient, QueueClient poisonQueueClient, QueueMessage message, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CogniLensDbContext>();
        var transcriptionService = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var qaAnalysisService = scope.ServiceProvider.GetRequiredService<IQaAnalysisService>();
        var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();

        if (message.DequeueCount > MaxAttempts)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Exceeded max delivery attempts");
            await DeadLetterAsync(db, queueClient, poisonQueueClient, message, "Exceeded max delivery attempts", stoppingToken);
            return "dead-lettered";
        }

        var jobStatus = await db.JobStatuses.FirstOrDefaultAsync(j => j.MessageId == message.MessageId, stoppingToken);
        if (jobStatus is null)
        {
            logger.LogWarning("No JobStatus found for message {MessageId}; discarding.", message.MessageId);
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return "discarded-no-job-status";
        }

        if (jobStatus.State == JobState.Completed)
        {
            // Duplicate delivery of a message we already finished processing.
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return "duplicate";
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AnalyzeJobMessage>(message.MessageText)
                ?? throw new InvalidOperationException("Empty analyze job payload.");

            activity?.SetTag("cognilens.call_id", payload.CallId);

            var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == payload.CallId, stoppingToken)
                ?? throw new InvalidOperationException($"Call {payload.CallId} not found.");

            jobStatus.State = JobState.Processing;
            jobStatus.AttemptCount = (int)message.DequeueCount;
            call.Status = CallStatus.Processing;
            await db.SaveChangesAsync(stoppingToken);

            var segments = await TraceStageAsync("transcribe",
                () => transcriptionService.TranscribeAsync(call.BlobUri, stoppingToken));

            db.TranscriptSegments.AddRange(segments.Select(s => new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                CallId = call.Id,
                SequenceNumber = s.SequenceNumber,
                SpeakerTag = s.SpeakerTag,
                Text = s.Text,
                StartTime = s.StartTime,
                EndTime = s.EndTime
            }));

            var chunks = TranscriptChunker.Chunk(segments);
            var chunkEmbeddings = await TraceStageAsync("embed",
                () => embeddingService.EmbedAsync(chunks.Select(c => c.Text).ToList(), stoppingToken));

            var rubrics = await db.Rubrics
                .Where(r => r.IsActive)
                .Select(r => new RubricDefinition(r.Id, r.Name, r.Description, r.Category))
                .ToListAsync(stoppingToken);

            var analysis = await TraceStageAsync("analyze",
                () => qaAnalysisService.AnalyzeAsync(segments, rubrics, stoppingToken));

            db.QaReports.Add(new QaReport
            {
                Id = Guid.NewGuid(),
                CallId = call.Id,
                Summary = analysis.Summary,
                SentimentJson = JsonSerializer.Serialize(analysis.Sentiment),
                RubricResultsJson = JsonSerializer.Serialize(analysis.RubricResults),
                NextBestAction = analysis.NextBestAction,
                CreatedAt = DateTimeOffset.UtcNow
            });

            var chunkDocuments = chunks.Select((c, i) => new TranscriptChunkDocument(
                Id: $"{call.Id}-{c.Index}",
                CallId: call.Id,
                OriginalFileName: call.OriginalFileName,
                ChunkIndex: c.Index,
                Text: c.Text,
                Embedding: chunkEmbeddings[i])).ToList();

            using (CogniLensTelemetry.ActivitySource.StartActivity("index-chunks"))
            {
                await searchIndexService.IndexChunksAsync(chunkDocuments, stoppingToken);
            }

            call.Status = CallStatus.Completed;
            call.ProcessedAt = DateTimeOffset.UtcNow;
            jobStatus.State = JobState.Completed;
            jobStatus.CompletedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(stoppingToken);

            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return "completed";
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Failed to process analyze job message {MessageId} (attempt {Attempt}).", message.MessageId, message.DequeueCount);
            jobStatus.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(stoppingToken);
            // Leave the message in the queue — its visibility timeout expires and Azure
            // Storage Queues redelivers it automatically, incrementing DequeueCount.
            return "failed";
        }
    }

    private async Task DeadLetterAsync(CogniLensDbContext db, QueueClient queueClient, QueueClient poisonQueueClient, QueueMessage message, string reason, CancellationToken stoppingToken)
    {
        var jobStatus = await db.JobStatuses.FirstOrDefaultAsync(j => j.MessageId == message.MessageId, stoppingToken);
        if (jobStatus is not null)
        {
            jobStatus.State = JobState.DeadLettered;
            jobStatus.ErrorMessage = reason;

            var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == jobStatus.CallId, stoppingToken);
            if (call is not null)
            {
                call.Status = CallStatus.Failed;
            }

            await db.SaveChangesAsync(stoppingToken);
        }

        await StorageRetryPipeline.Instance.ExecuteAsync(
            async ct => await poisonQueueClient.SendMessageAsync(message.MessageText, cancellationToken: ct),
            stoppingToken);

        await DeleteMessageAsync(queueClient, message, stoppingToken);

        logger.LogWarning("Dead-lettered message {MessageId}: {Reason}", message.MessageId, reason);
    }

    private async Task RefreshQueueDepthAsync(QueueClient queueClient, CancellationToken stoppingToken)
    {
        try
        {
            var properties = await queueClient.GetPropertiesAsync(stoppingToken);
            Interlocked.Exchange(ref _approximateQueueDepth, properties.Value.ApproximateMessagesCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not retried and not fatal. This is a telemetry read: failing the poll
            // loop — and therefore stopping all job processing — because a metric could not be
            // sampled would be a strictly worse outcome than a gap in a chart.
            logger.LogDebug(ex, "Could not sample analyze queue depth.");
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueDepth()
    {
        var depth = Interlocked.Read(ref _approximateQueueDepth);

        // -1 means nothing has been sampled yet — the first collection after a cold start, or
        // after GetProperties failed. Emitting nothing is the honest answer; emitting 0 would
        // read as "the backlog is drained", which is the one wrong conclusion that would stop
        // someone investigating.
        if (depth < 0)
        {
            yield break;
        }

        yield return new Measurement<long>(depth, new KeyValuePair<string, object?>("queue", _options.QueueName));
    }

    /// <summary>
    /// Wraps one pipeline stage in its own span. The Azure SDK already traces the individual HTTP
    /// calls underneath, but transcription and analysis are multi-call operations (polling loops,
    /// chunked completions) whose aggregate duration is the number worth looking at — and the
    /// number Phase 5's per-stage metrics are derived from.
    /// </summary>
    private static async Task<T> TraceStageAsync<T>(string stageName, Func<Task<T>> stage)
    {
        using var activity = CogniLensTelemetry.ActivitySource.StartActivity(stageName);
        return await stage();
    }

    /// <summary>
    /// Opens the consumer span, parented to the Api request that enqueued the message so the
    /// upload, the queue hop and the whole analysis pipeline read as one trace.
    /// </summary>
    private Activity? StartConsumerActivity(QueueMessage message)
    {
        var activityName = $"{_options.QueueName} process";

        string? traceParent = null;
        try
        {
            traceParent = JsonSerializer.Deserialize<AnalyzeJobMessage>(message.MessageText)?.TraceParent;
        }
        catch (JsonException)
        {
            // A malformed body is a genuine failure, but not this method's to report: the real
            // deserialise in ProcessMessageAsync raises it inside the try/catch that records it
            // on JobStatus. Swallowing it here only costs the trace parent, and throwing would
            // escape ProcessMessageAsync entirely and kill the polling loop.
        }

        // Null traceParent is the normal case for a message enqueued before this field existed,
        // or one enqueued with no ambient trace. The two overloads are separated explicitly
        // rather than passing a null string, because the (string parentId) overload's behaviour
        // on null is an implementation detail worth not depending on.
        var activity = traceParent is null
            ? CogniLensTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Consumer)
            : CogniLensTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Consumer, traceParent);

        activity?.SetTag("messaging.system", "azure_queue_storage");
        activity?.SetTag("messaging.operation", "process");
        activity?.SetTag("messaging.destination.name", _options.QueueName);
        activity?.SetTag("messaging.message.id", message.MessageId);
        // Surfaces redelivery directly on the span — a job at attempt 3 looks identical to one at
        // attempt 1 in the logs otherwise, right up until it dead-letters.
        activity?.SetTag("messaging.message.delivery_count", message.DequeueCount);

        return activity;
    }

    private static async Task DeleteMessageAsync(QueueClient queueClient, QueueMessage message, CancellationToken stoppingToken) =>
        await StorageRetryPipeline.Instance.ExecuteAsync(
            async ct => await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken: ct),
            stoppingToken);
}
