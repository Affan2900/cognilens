namespace CogniLens.Core.Contracts;

public interface IBlobStorageService
{
    Task<BlobUploadTicket> CreateUploadTicketAsync(Guid callId, string fileName, CancellationToken cancellationToken);

    // Read-only SAS so external Azure services (Speech batch transcription) can fetch the
    // blob directly, without proxying the audio bytes through our own services.
    Task<string> GenerateReadSasUriAsync(string blobUri, TimeSpan validFor, CancellationToken cancellationToken);
}

public record BlobUploadTicket(string BlobUri, string UploadUrl, DateTimeOffset ExpiresAt);
