using System.Diagnostics;
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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30);

    private readonly StorageOptions _options = storageOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueClient = queueServiceClient.GetQueueClient(_options.QueueName);
        var poisonQueueClient = queueServiceClient.GetQueueClient(_options.PoisonQueueName);

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
            QueueMessage[] messages;
            try
            {
                messages = await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
                {
                    var response = await queueClient.ReceiveMessagesAsync(maxMessages: 5, visibilityTimeout: VisibilityTimeout, cancellationToken: ct);
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

    private async Task ProcessMessageAsync(QueueClient queueClient, QueueClient poisonQueueClient, QueueMessage message, CancellationToken stoppingToken)
    {
        // Started before anything else in the method so every branch below — dead-letter,
        // orphaned-message discard, duplicate delivery, success — lands inside the span rather
        // than only the happy path.
        using var activity = StartConsumerActivity(message);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CogniLensDbContext>();
        var transcriptionService = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var qaAnalysisService = scope.ServiceProvider.GetRequiredService<IQaAnalysisService>();
        var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();

        if (message.DequeueCount > MaxAttempts)
        {
            activity?.SetTag("cognilens.outcome", "dead-lettered");
            activity?.SetStatus(ActivityStatusCode.Error, "Exceeded max delivery attempts");
            await DeadLetterAsync(db, queueClient, poisonQueueClient, message, "Exceeded max delivery attempts", stoppingToken);
            return;
        }

        var jobStatus = await db.JobStatuses.FirstOrDefaultAsync(j => j.MessageId == message.MessageId, stoppingToken);
        if (jobStatus is null)
        {
            activity?.SetTag("cognilens.outcome", "discarded-no-job-status");
            logger.LogWarning("No JobStatus found for message {MessageId}; discarding.", message.MessageId);
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return;
        }

        if (jobStatus.State == JobState.Completed)
        {
            // Duplicate delivery of a message we already finished processing.
            activity?.SetTag("cognilens.outcome", "duplicate");
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return;
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

            activity?.SetTag("cognilens.outcome", "completed");
            call.Status = CallStatus.Completed;
            call.ProcessedAt = DateTimeOffset.UtcNow;
            jobStatus.State = JobState.Completed;
            jobStatus.CompletedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(stoppingToken);

            await DeleteMessageAsync(queueClient, message, stoppingToken);
        }
        catch (Exception ex)
        {
            activity?.SetTag("cognilens.outcome", "failed");
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Failed to process analyze job message {MessageId} (attempt {Attempt}).", message.MessageId, message.DequeueCount);
            jobStatus.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(stoppingToken);
            // Leave the message in the queue — its visibility timeout expires and Azure
            // Storage Queues redelivers it automatically, incrementing DequeueCount.
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
