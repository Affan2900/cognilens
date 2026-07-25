using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using CogniLens.Core.Contracts;
using CogniLens.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace CogniLens.Infrastructure.AzureAi;

public class AzureOpenAiQaAnalysisService(
    AzureOpenAIClient client,
    IOptions<AzureAiOptions> options,
    ILogger<AzureOpenAiQaAnalysisService> logger) : IQaAnalysisService
{
    private readonly OpenAiOptions _options = options.Value.OpenAi;

    private static readonly BinaryData Schema = BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "sentiment": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "speaker": { "type": "string" },
                  "sentiment": { "type": "string", "enum": ["positive", "neutral", "negative"] },
                  "rationale": { "type": "string" }
                },
                "required": ["speaker", "sentiment", "rationale"],
                "additionalProperties": false
              }
            },
            "rubricResults": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "rubricId": { "type": "string" },
                  "result": { "type": "string", "enum": ["pass", "fail", "not_applicable"] },
                  "evidence": { "type": ["string", "null"] }
                },
                "required": ["rubricId", "result", "evidence"],
                "additionalProperties": false
              }
            },
            "nextBestAction": { "type": "string" }
          },
          "required": ["summary", "sentiment", "rubricResults", "nextBestAction"],
          "additionalProperties": false
        }
        """);

    public async Task<QaAnalysisResult> AnalyzeAsync(
        IReadOnlyList<TranscribedSegment> segments,
        IReadOnlyList<RubricDefinition> rubrics,
        CancellationToken cancellationToken)
    {
        var chatClient = client.GetChatClient(_options.ChatDeploymentName);

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "qa_analysis", Schema, "Structured QA analysis of a contact-centre call.", jsonSchemaIsStrict: true)
        };

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(BuildSystemPrompt(rubrics)),
            ChatMessage.CreateUserMessage(BuildTranscriptPrompt(segments))
        };

        var rawOutput = await CallModelAsync(chatClient, messages, options, cancellationToken);

        var result = TryParse(rawOutput, rubrics, out var parsed, out var validationError);
        if (result)
        {
            return parsed!;
        }

        logger.LogWarning("QA analysis schema validation failed on first attempt: {Error}. Retrying once.", validationError);

        messages.Add(ChatMessage.CreateAssistantMessage(rawOutput));
        messages.Add(ChatMessage.CreateUserMessage(
            $"Your previous response was invalid: {validationError}. Reply again with ONLY a JSON object matching the schema."));

        var retryOutput = await CallModelAsync(chatClient, messages, options, cancellationToken);

        if (TryParse(retryOutput, rubrics, out parsed, out validationError))
        {
            return parsed!;
        }

        logger.LogError(
            "QA analysis schema validation failed twice. Raw output: {RawOutput}", retryOutput);
        throw new InvalidOperationException($"QA analysis failed schema validation after retry: {validationError}");
    }

    private static async Task<string> CallModelAsync(
        ChatClient chatClient, IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var completion = await AiRetryPipeline.Instance.ExecuteAsync(
            async ct => await chatClient.CompleteChatAsync(messages, options, ct),
            cancellationToken);

        return completion.Value.Content[0].Text;
    }

    private static string BuildSystemPrompt(IReadOnlyList<RubricDefinition> rubrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a contact-centre QA analyst. You will be given a diarized call transcript.");
        sb.AppendLine("Produce: a concise summary, sentiment per speaker, a pass/fail/not_applicable result");
        sb.AppendLine("for each rubric below (with a verbatim quoted evidence span from the transcript when");
        sb.AppendLine("the result is pass or fail, or null when not_applicable), and a next-best-action.");
        sb.AppendLine("Echo each rubric's id back exactly as \"rubricId\" in your response.");
        sb.AppendLine();
        sb.AppendLine("Rubrics:");
        foreach (var rubric in rubrics)
        {
            sb.AppendLine($"- id={rubric.Id}, name=\"{rubric.Name}\", category=\"{rubric.Category}\": {rubric.Description}");
        }

        return sb.ToString();
    }

    private static string BuildTranscriptPrompt(IReadOnlyList<TranscribedSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Transcript:");
        foreach (var segment in segments.OrderBy(s => s.SequenceNumber))
        {
            sb.AppendLine($"[Speaker {segment.SpeakerTag}] {segment.Text}");
        }

        return sb.ToString();
    }

    private static bool TryParse(
        string rawOutput,
        IReadOnlyList<RubricDefinition> rubrics,
        out QaAnalysisResult? result,
        out string? validationError)
    {
        result = null;

        ModelResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelResponse>(rawOutput);
        }
        catch (JsonException ex)
        {
            validationError = $"Response was not valid JSON: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            validationError = "Response deserialized to null.";
            return false;
        }

        var knownRubricIds = rubrics.Select(r => r.Id).ToHashSet();
        var rubricResults = new List<RubricResult>();
        foreach (var r in parsed.RubricResults)
        {
            if (!Guid.TryParse(r.RubricId, out var rubricId) || !knownRubricIds.Contains(rubricId))
            {
                validationError = $"Unknown or malformed rubricId '{r.RubricId}'.";
                return false;
            }

            if (r.Result is not ("pass" or "fail" or "not_applicable"))
            {
                validationError = $"Invalid rubric result value '{r.Result}'.";
                return false;
            }

            rubricResults.Add(new RubricResult(rubricId, r.Result, r.Evidence));
        }

        if (rubricResults.Count != knownRubricIds.Count)
        {
            validationError = $"Expected {knownRubricIds.Count} rubric results, got {rubricResults.Count}.";
            return false;
        }

        var sentiment = parsed.Sentiment
            .Select(s => new SpeakerSentiment(s.Speaker, s.Sentiment, s.Rationale))
            .ToList();

        result = new QaAnalysisResult(parsed.Summary, sentiment, rubricResults, parsed.NextBestAction);
        validationError = null;
        return true;
    }

    private record ModelResponse(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("sentiment")] IReadOnlyList<ModelSentiment> Sentiment,
        [property: JsonPropertyName("rubricResults")] IReadOnlyList<ModelRubricResult> RubricResults,
        [property: JsonPropertyName("nextBestAction")] string NextBestAction);

    private record ModelSentiment(
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("sentiment")] string Sentiment,
        [property: JsonPropertyName("rationale")] string Rationale);

    private record ModelRubricResult(
        [property: JsonPropertyName("rubricId")] string RubricId,
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("evidence")] string? Evidence);
}
