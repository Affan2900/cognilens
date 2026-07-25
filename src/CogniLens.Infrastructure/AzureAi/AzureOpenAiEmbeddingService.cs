using Azure.AI.OpenAI;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.Resilience;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure.AzureAi;

public class AzureOpenAiEmbeddingService(AzureOpenAIClient client, IOptions<AzureAiOptions> options) : IEmbeddingService
{
    private readonly OpenAiOptions _options = options.Value.OpenAi;

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var embeddingClient = client.GetEmbeddingClient(_options.EmbeddingDeploymentName);

        var result = await AiRetryPipeline.Instance.ExecuteAsync(
            async ct => await embeddingClient.GenerateEmbeddingsAsync(texts, cancellationToken: ct),
            cancellationToken);

        return result.Value
            .OrderBy(e => e.Index)
            .Select(e => e.ToFloats())
            .ToList();
    }
}
