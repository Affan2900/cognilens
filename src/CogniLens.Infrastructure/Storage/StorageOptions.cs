namespace CogniLens.Infrastructure.Storage;

public class StorageOptions
{
    public required string ConnectionString { get; set; }
    public string ContainerName { get; set; } = "call-audio";
    public string QueueName { get; set; } = "analyze-jobs";
    public string PoisonQueueName { get; set; } = "analyze-jobs-poison";

    // The SAS URI is generated against the account's internal endpoint (which, in docker-compose,
    // is the "azurite" service hostname and unreachable from outside the compose network). This
    // is used to rewrite only the scheme/host/port before handing the SAS URL to a caller — the
    // SAS signature itself does not cover the Host header, so the rewrite doesn't invalidate it.
    public string? PublicBlobEndpoint { get; set; }
}
