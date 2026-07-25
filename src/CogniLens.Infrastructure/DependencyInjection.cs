using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using CogniLens.Core.Contracts;
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

        return services;
    }
}
