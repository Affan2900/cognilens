namespace CogniLens.Core.Contracts;

public interface ISearchIndexService
{
    Task EnsureIndexExistsAsync(CancellationToken cancellationToken);

    Task IndexChunksAsync(IReadOnlyList<TranscriptChunkDocument> chunks, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, int top, CancellationToken cancellationToken);
}

public record TranscriptChunkDocument(
    string Id,
    Guid CallId,
    string OriginalFileName,
    int ChunkIndex,
    string Text,
    ReadOnlyMemory<float> Embedding);

public record SearchResultItem(
    string Id,
    Guid CallId,
    string OriginalFileName,
    int ChunkIndex,
    string Text,
    double Score);
