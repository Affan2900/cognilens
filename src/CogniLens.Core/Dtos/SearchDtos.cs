namespace CogniLens.Core.Dtos;

public record SearchResultDto(
    Guid CallId,
    string OriginalFileName,
    int ChunkIndex,
    string Text,
    double Score);
