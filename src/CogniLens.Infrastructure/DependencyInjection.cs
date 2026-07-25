using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.AzureAi;
using CogniLens.Infrastructure.Persistence;
using CogniLens.Infrastructure.Queues;
using CogniLens.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCogniLensInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CogniLensDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("CogniLensDb")));

        services.Configure<StorageOptions>(configuration.GetSection("Storage"));

        services.AddSingleton(sp =>
            new BlobServiceClient(sp.GetRequiredService<IOptions<StorageOptions>>().Value.ConnectionString));
        services.AddSingleton(sp =>
            new QueueServiceClient(sp.GetRequiredService<IOptions<StorageOptions>>().Value.ConnectionString));

        services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
        services.AddScoped<IJobQueue, AzureQueueJobQueue>();

        services.Configure<AzureAiOptions>(configuration.GetSection("AzureAi"));

        // Same credential type locally (via `az login`) and in Container Apps (via Managed
        // Identity) — DefaultAzureCredential picks the right one for the environment it's
        // running in, so there's no separate code path to maintain for Phase 3.
        services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

        services.AddSingleton(sp =>
        {
            var aiOptions = sp.GetRequiredService<IOptions<AzureAiOptions>>().Value;
            return new AzureOpenAIClient(new Uri(aiOptions.OpenAi.Endpoint), sp.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton(sp =>
        {
            var aiOptions = sp.GetRequiredService<IOptions<AzureAiOptions>>().Value;
            return new SearchIndexClient(new Uri(aiOptions.Search.Endpoint), sp.GetRequiredService<TokenCredential>());
        });

        services.AddHttpClient<ITranscriptionService, AzureSpeechTranscriptionService>();

        services.AddScoped<IEmbeddingService, AzureOpenAiEmbeddingService>();
        services.AddScoped<IQaAnalysisService, AzureOpenAiQaAnalysisService>();
        services.AddScoped<ISearchIndexService, AzureAiSearchIndexService>();

        return services;
    }
}
