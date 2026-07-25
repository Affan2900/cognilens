namespace CogniLens.Core.Contracts;

public interface IBlobStorageService
{
    Task<BlobUploadTicket> CreateUploadTicketAsync(Guid callId, string fileName, CancellationToken cancellationToken);
}

public record BlobUploadTicket(string BlobUri, string UploadUrl, DateTimeOffset ExpiresAt);
