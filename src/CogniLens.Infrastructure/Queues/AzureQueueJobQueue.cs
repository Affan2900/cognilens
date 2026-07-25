using System.Text.Json;
using Azure.Storage.Queues;
using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
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

        return await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            await queueClient.CreateIfNotExistsAsync(cancellationToken: ct);

            var body = JsonSerializer.Serialize(new AnalyzeJobMessage(callId));
            var receipt = await queueClient.SendMessageAsync(body, cancellationToken: ct);
            return receipt.Value.MessageId;
        }, cancellationToken);
    }
}
