using System.Text.Json.Serialization;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.Resilience;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure.AzureAi;

public class AzureAiSearchIndexService(
    SearchIndexClient indexClient,
    IEmbeddingService embeddingService,
    IOptions<AzureAiOptions> options) : ISearchIndexService
{
    private const int EmbeddingDimensions = 1536; // text-embedding-3-small
    private const string VectorProfileName = "cognilens-vector-profile";
    private const string VectorAlgorithmName = "cognilens-hnsw";
    private const string SemanticConfigName = "cognilens-semantic-config";
    private const string VectorFieldName = "embedding";

    private readonly AiSearchOptions _options = options.Value.Search;

    public async Task EnsureIndexExistsAsync(CancellationToken cancellationToken)
    {
        var index = new SearchIndex(_options.IndexName)
        {
            Fields =
            {
                new SearchField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchField("callId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchField("originalFileName", SearchFieldDataType.String) { IsSearchable = true, IsFilterable = true },
                new SearchField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
                new SearchField("text", SearchFieldDataType.String) { IsSearchable = true },
                new SearchField(VectorFieldName, SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = EmbeddingDimensions,
                    VectorSearchProfileName = VectorProfileName
                }
            },
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfileName, VectorAlgorithmName) },
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgorithmName) }
            },
            SemanticSearch = new SemanticSearch
            {
                DefaultConfigurationName = SemanticConfigName,
                Configurations =
                {
                    new SemanticConfiguration(
                        SemanticConfigName,
                        new SemanticPrioritizedFields { ContentFields = { new SemanticField("text") } })
                }
            }
        };

        await AiRetryPipeline.Instance.ExecuteAsync(
            async ct => await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct),
            cancellationToken);
    }

    public async Task IndexChunksAsync(IReadOnlyList<TranscriptChunkDocument> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var searchClient = indexClient.GetSearchClient(_options.IndexName);

        var documents = chunks.Select(c => new SearchDocument(
            c.Id, c.CallId.ToString(), c.OriginalFileName, c.ChunkIndex, c.Text, c.Embedding.ToArray()));

        await AiRetryPipeline.Instance.ExecuteAsync(
            async ct => await searchClient.MergeOrUploadDocumentsAsync(documents, cancellationToken: ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, int top, CancellationToken cancellationToken)
    {
        var searchClient = indexClient.GetSearchClient(_options.IndexName);

        var queryEmbeddings = await embeddingService.EmbedAsync([query], cancellationToken);
        var queryVector = queryEmbeddings[0];

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = top,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = SemanticConfigName },
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(queryVector) { Fields = { VectorFieldName }, KNearestNeighborsCount = top } }
            }
        };
        searchOptions.Select.Add("id");
        searchOptions.Select.Add("callId");
        searchOptions.Select.Add("originalFileName");
        searchOptions.Select.Add("chunkIndex");
        searchOptions.Select.Add("text");

        var response = await AiRetryPipeline.Instance.ExecuteAsync(
            async ct => await searchClient.SearchAsync<SearchDocument>(query, searchOptions, ct),
            cancellationToken);

        var results = new List<SearchResultItem>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            results.Add(new SearchResultItem(
                result.Document.Id,
                Guid.Parse(result.Document.CallId),
                result.Document.OriginalFileName,
                result.Document.ChunkIndex,
                result.Document.Text,
                result.Score ?? 0));
        }

        return results;
    }

    private record SearchDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("callId")] string CallId,
        [property: JsonPropertyName("originalFileName")] string OriginalFileName,
        [property: JsonPropertyName("chunkIndex")] int ChunkIndex,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
