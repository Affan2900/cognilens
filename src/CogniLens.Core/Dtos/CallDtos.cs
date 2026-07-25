using CogniLens.Core.Enums;

namespace CogniLens.Core.Dtos;

public record CreateCallRequest(string OriginalFileName);

public record CreateCallResponse(Guid CallId, string UploadUrl, string BlobUri, DateTimeOffset ExpiresAt);

public record AnalyzeCallResponse(Guid CallId, CallStatus Status);

public record CallStatusResponse(
    Guid CallId,
    string OriginalFileName,
    CallStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    IReadOnlyList<TranscriptSegmentDto> Transcript,
    QaReportDto? Report);

public record QaReportDto(string Summary, string? SentimentJson, string? RubricResultsJson, string? NextBestAction);

public record TranscriptSegmentDto(
    int SequenceNumber,
    int SpeakerTag,
    string Text,
    TimeSpan StartTime,
    TimeSpan EndTime);
