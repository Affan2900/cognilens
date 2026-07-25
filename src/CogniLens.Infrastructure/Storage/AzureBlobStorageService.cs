using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.Resilience;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure.Storage;

public class AzureBlobStorageService(BlobServiceClient blobServiceClient, IOptions<StorageOptions> options)
    : IBlobStorageService
{
    private readonly StorageOptions _options = options.Value;

    public async Task<BlobUploadTicket> CreateUploadTicketAsync(Guid callId, string fileName, CancellationToken cancellationToken)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.ContainerName);

        return await StorageRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

            var blobClient = containerClient.GetBlobClient($"{callId}/{fileName}");
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
            var sasUri = blobClient.GenerateSasUri(BlobSasPermissions.Write | BlobSasPermissions.Create, expiresAt);

            if (!string.IsNullOrEmpty(_options.PublicBlobEndpoint))
            {
                var publicBase = new Uri(_options.PublicBlobEndpoint);
                sasUri = new UriBuilder(sasUri)
                {
                    Scheme = publicBase.Scheme,
                    Host = publicBase.Host,
                    Port = publicBase.Port
                }.Uri;
            }

            return new BlobUploadTicket(blobClient.Uri.ToString(), sasUri.ToString(), expiresAt);
        }, cancellationToken);
    }
}
