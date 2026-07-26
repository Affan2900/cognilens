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

        // Same credential type locally (via `az login`) and in Container Apps (via Managed
        // Identity) — DefaultAzureCredential picks the right one for the environment it's
        // running in, so there's no separate code path to maintain for Phase 3.
        services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

        // Dev (Azurite) uses a shared-key connection string; Container Apps has none configured
        // and instead gets a bare service URI, authenticated via the container's Managed Identity.
        //
        // The connection string wins when both are present, which makes a stray dev default a
        // silent production outage rather than a startup failure: a `UseDevelopmentStorage=true`
        // left in appsettings.json sent the deployed Worker to 127.0.0.1:10001 while every
        // Storage__*ServiceUri env var sat there correctly and ignored. ResolveStorageMode makes
        // that combination refuse to start instead, and says which setting to remove.
        services.AddSingleton(sp =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            return ResolveStorageMode(storageOptions, storageOptions.BlobServiceUri, nameof(StorageOptions.BlobServiceUri)) is { } blobUri
                ? new BlobServiceClient(blobUri, sp.GetRequiredService<TokenCredential>())
                : new BlobServiceClient(storageOptions.ConnectionString);
        });
        services.AddSingleton(sp =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            return ResolveStorageMode(storageOptions, storageOptions.QueueServiceUri, nameof(StorageOptions.QueueServiceUri)) is { } queueUri
                ? new QueueServiceClient(queueUri, sp.GetRequiredService<TokenCredential>())
                : new QueueServiceClient(storageOptions.ConnectionString);
        });

        services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
        services.AddScoped<IJobQueue, AzureQueueJobQueue>();

        services.Configure<AzureAiOptions>(configuration.GetSection("AzureAi"));

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

    /// <summary>
    /// Decides how a storage client should authenticate, and refuses ambiguity.
    /// Returns the service URI when Managed Identity should be used, or <c>null</c> when the
    /// shared-key connection string should be used.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when both settings are present (ambiguous — the connection string would silently
    /// win and the URI would be ignored) or when neither is (nothing to connect to).
    /// </exception>
    private static Uri? ResolveStorageMode(StorageOptions options, string? serviceUri, string serviceUriName)
    {
        var hasConnectionString = !string.IsNullOrEmpty(options.ConnectionString);
        var hasServiceUri = !string.IsNullOrWhiteSpace(serviceUri);

        if (hasConnectionString && hasServiceUri)
        {
            throw new InvalidOperationException(
                $"Storage is configured with both a ConnectionString and a {serviceUriName}. " +
                "These are mutually exclusive: the connection string takes effect and the service " +
                $"URI is ignored, so this app would reach the address baked into the connection " +
                $"string instead of {serviceUri}. Remove Storage:ConnectionString from the deployed " +
                "configuration to use Managed Identity. (The connection string value is deliberately " +
                "not echoed here — it can carry an account key.)");
        }

        if (!hasConnectionString && !hasServiceUri)
        {
            throw new InvalidOperationException(
                $"Storage has neither a ConnectionString nor a {serviceUriName} configured. " +
                $"Set Storage__{serviceUriName} for Managed Identity, or Storage__ConnectionString " +
                "for the local Azurite emulator.");
        }

        return hasServiceUri ? new Uri(serviceUri!) : null;
    }
}
