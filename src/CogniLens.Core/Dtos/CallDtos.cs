using CogniLens.Core.Enums;

namespace CogniLens.Core.Dtos;

public record CreateCallRequest(string OriginalFileName);

public record CreateCallResponse(Guid CallId, string UploadUrl, string BlobUri, DateTimeOffset ExpiresAt);

public record AnalyzeCallResponse(Guid CallId, CallStatus Status);

public record CallStatusResponse(
    Guid CallId,
    CallStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    QaReportDto? Report);

public record QaReportDto(string Summary, string? SentimentJson, string? RubricResultsJson, string? NextBestAction);
