namespace CogniLens.Core.Contracts;

public interface IEmbeddingService
{
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
