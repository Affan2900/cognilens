namespace CogniLens.Infrastructure.AzureAi;

public class AzureAiOptions
{
    public required SpeechOptions Speech { get; set; }
    public required OpenAiOptions OpenAi { get; set; }
    public required AiSearchOptions Search { get; set; }
}

public class SpeechOptions
{
    // Custom-subdomain endpoint (e.g. https://cognilens-speech-dev.cognitiveservices.azure.com/),
    // which is required for AAD/token-credential auth rather than key-only auth.
    public required string Endpoint { get; set; }
    public string Locale { get; set; } = "en-US";
}

public class OpenAiOptions
{
    public required string Endpoint { get; set; }
    public string ChatDeploymentName { get; set; } = "gpt-5-mini";
    public string EmbeddingDeploymentName { get; set; } = "text-embedding-3-small";
}

public class AiSearchOptions
{
    public required string Endpoint { get; set; }
    public string IndexName { get; set; } = "transcript-chunks";
}
