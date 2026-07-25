using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using CogniLens.Core.Chunking;
using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
using CogniLens.Core.Entities;
using CogniLens.Core.Enums;
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
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CogniLensDbContext>();
        var transcriptionService = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var qaAnalysisService = scope.ServiceProvider.GetRequiredService<IQaAnalysisService>();
        var searchIndexService = scope.ServiceProvider.GetRequiredService<ISearchIndexService>();

        if (message.DequeueCount > MaxAttempts)
        {
            await DeadLetterAsync(db, queueClient, poisonQueueClient, message, "Exceeded max delivery attempts", stoppingToken);
            return;
        }

        var jobStatus = await db.JobStatuses.FirstOrDefaultAsync(j => j.MessageId == message.MessageId, stoppingToken);
        if (jobStatus is null)
        {
            logger.LogWarning("No JobStatus found for message {MessageId}; discarding.", message.MessageId);
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return;
        }

        if (jobStatus.State == JobState.Completed)
        {
            // Duplicate delivery of a message we already finished processing.
            await DeleteMessageAsync(queueClient, message, stoppingToken);
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AnalyzeJobMessage>(message.MessageText)
                ?? throw new InvalidOperationException("Empty analyze job payload.");

            var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == payload.CallId, stoppingToken)
                ?? throw new InvalidOperationException($"Call {payload.CallId} not found.");

            jobStatus.State = JobState.Processing;
            jobStatus.AttemptCount = (int)message.DequeueCount;
            call.Status = CallStatus.Processing;
            await db.SaveChangesAsync(stoppingToken);

            var segments = await transcriptionService.TranscribeAsync(call.BlobUri, stoppingToken);

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
            var chunkEmbeddings = await embeddingService.EmbedAsync(
                chunks.Select(c => c.Text).ToList(), stoppingToken);

            var rubrics = await db.Rubrics
                .Where(r => r.IsActive)
                .Select(r => new RubricDefinition(r.Id, r.Name, r.Description, r.Category))
                .ToListAsync(stoppingToken);

            var analysis = await qaAnalysisService.AnalyzeAsync(segments, rubrics, stoppingToken);

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

            await searchIndexService.IndexChunksAsync(chunkDocuments, stoppingToken);

            call.Status = CallStatus.Completed;
            call.ProcessedAt = DateTimeOffset.UtcNow;
            jobStatus.State = JobState.Completed;
            jobStatus.CompletedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(stoppingToken);

            await DeleteMessageAsync(queueClient, message, stoppingToken);
        }
        catch (Exception ex)
        {
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

    private static async Task DeleteMessageAsync(QueueClient queueClient, QueueMessage message, CancellationToken stoppingToken) =>
        await StorageRetryPipeline.Instance.ExecuteAsync(
            async ct => await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken: ct),
            stoppingToken);
}
