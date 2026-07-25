using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Azure.Core;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CogniLens.Infrastructure.AzureAi;

// There is no batch-transcription surface in the real-time Speech SDK, so this talks to the
// Speech-to-text REST API (v3.1) directly, authenticating with a bearer token from the same
// TokenCredential used everywhere else (no keys).
public class AzureSpeechTranscriptionService(
    HttpClient httpClient,
    TokenCredential credential,
    IBlobStorageService blobStorageService,
    IOptions<AzureAiOptions> options,
    ILogger<AzureSpeechTranscriptionService> logger) : ITranscriptionService
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(15);

    private readonly SpeechOptions _options = options.Value.Speech;

    public async Task<IReadOnlyList<TranscribedSegment>> TranscribeAsync(string blobUri, CancellationToken cancellationToken)
    {
        var contentUrl = await blobStorageService.GenerateReadSasUriAsync(blobUri, TimeSpan.FromHours(2), cancellationToken);

        var transcriptionUrl = await CreateTranscriptionJobAsync(contentUrl, cancellationToken);
        try
        {
            await WaitForCompletionAsync(transcriptionUrl, cancellationToken);
            return await FetchSegmentsAsync(transcriptionUrl, cancellationToken);
        }
        finally
        {
            await DeleteTranscriptionJobAsync(transcriptionUrl, cancellationToken);
        }
    }

    private async Task<string> CreateTranscriptionJobAsync(string contentUrl, CancellationToken cancellationToken)
    {
        var body = new
        {
            contentUrls = new[] { contentUrl },
            locale = _options.Locale,
            displayName = $"cognilens-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            properties = new
            {
                diarizationEnabled = true,
                punctuationMode = "DictatedAndAutomatic",
                profanityFilterMode = "None"
            }
        };

        var response = await AiRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, "speechtotext/v3.1/transcriptions", ct);
            request.Content = JsonContent.Create(body);
            return await httpClient.SendAsync(request, ct);
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var location = response.Headers.Location
            ?? throw new InvalidOperationException("Speech batch transcription response did not include a Location header.");

        return location.ToString();
    }

    private async Task WaitForCompletionAsync(string transcriptionUrl, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PollTimeout);

        while (true)
        {
            var status = await AiRetryPipeline.Instance.ExecuteAsync(async ct =>
            {
                using var request = await CreateRequestAsync(HttpMethod.Get, transcriptionUrl, ct, absoluteUrl: true);
                using var response = await httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<TranscriptionStatusResponse>(cancellationToken: ct);
            }, cancellationToken);

            switch (status?.Status)
            {
                case "Succeeded":
                    return;
                case "Failed":
                    throw new InvalidOperationException($"Speech batch transcription failed: {JsonSerializer.Serialize(status.Properties)}");
                default:
                    logger.LogInformation("Speech transcription {Url} status: {Status}", transcriptionUrl, status?.Status);
                    break;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Speech batch transcription {transcriptionUrl} did not complete within {PollTimeout}.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<TranscribedSegment>> FetchSegmentsAsync(string transcriptionUrl, CancellationToken cancellationToken)
    {
        var files = await AiRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, $"{transcriptionUrl}/files", ct, absoluteUrl: true);
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TranscriptionFilesResponse>(cancellationToken: ct);
        }, cancellationToken);

        var resultFile = files?.Values.FirstOrDefault(f => f.Kind == "Transcription")
            ?? throw new InvalidOperationException($"No Transcription result file found for {transcriptionUrl}.");

        var contentUrl = resultFile.Links.ContentUrl;

        var result = await AiRetryPipeline.Instance.ExecuteAsync(async ct =>
        {
            using var response = await httpClient.GetAsync(contentUrl, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TranscriptionResultDocument>(cancellationToken: ct);
        }, cancellationToken);

        var phrases = result?.RecognizedPhrases ?? [];

        return phrases
            .OrderBy(p => XmlConvert.ToTimeSpan(p.Offset))
            .Select((phrase, index) => new TranscribedSegment(
                SequenceNumber: index,
                SpeakerTag: phrase.Speaker,
                Text: phrase.NBest.FirstOrDefault()?.Display ?? string.Empty,
                StartTime: XmlConvert.ToTimeSpan(phrase.Offset),
                EndTime: XmlConvert.ToTimeSpan(phrase.Offset) + XmlConvert.ToTimeSpan(phrase.Duration)))
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .ToList();
    }

    private async Task DeleteTranscriptionJobAsync(string transcriptionUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Delete, transcriptionUrl, cancellationToken, absoluteUrl: true);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to delete Speech transcription job {Url}: {Status}", transcriptionUrl, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete Speech transcription job {Url}.", transcriptionUrl);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string urlOrPath, CancellationToken cancellationToken, bool absoluteUrl = false)
    {
        var uri = absoluteUrl ? urlOrPath : new Uri(new Uri(_options.Endpoint), urlOrPath).ToString();
        var request = new HttpRequestMessage(method, uri);

        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        return request;
    }

    private record TranscriptionStatusResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("properties")] JsonElement? Properties);

    private record TranscriptionFilesResponse(
        [property: JsonPropertyName("values")] IReadOnlyList<TranscriptionFile> Values);

    private record TranscriptionFile(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("links")] TranscriptionFileLinks Links);

    private record TranscriptionFileLinks(
        [property: JsonPropertyName("contentUrl")] string ContentUrl);

    private record TranscriptionResultDocument(
        [property: JsonPropertyName("recognizedPhrases")] IReadOnlyList<RecognizedPhrase> RecognizedPhrases);

    private record RecognizedPhrase(
        [property: JsonPropertyName("speaker")] int Speaker,
        [property: JsonPropertyName("offset")] string Offset,
        [property: JsonPropertyName("duration")] string Duration,
        [property: JsonPropertyName("nBest")] IReadOnlyList<NBestResult> NBest);

    private record NBestResult(
        [property: JsonPropertyName("display")] string Display);
}
