namespace CogniLens.Core.Contracts;

public interface IQaAnalysisService
{
    Task<QaAnalysisResult> AnalyzeAsync(
        IReadOnlyList<TranscribedSegment> segments,
        IReadOnlyList<RubricDefinition> rubrics,
        CancellationToken cancellationToken);
}

public record RubricDefinition(Guid Id, string Name, string Description, string Category);

public record SpeakerSentiment(string Speaker, string Sentiment, string Rationale);

public record RubricResult(Guid RubricId, string Result, string? Evidence);

public record QaAnalysisResult(
    string Summary,
    IReadOnlyList<SpeakerSentiment> Sentiment,
    IReadOnlyList<RubricResult> RubricResults,
    string NextBestAction);
