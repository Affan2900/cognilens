namespace CogniLens.Core.Entities;

public class QaReport
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public Call Call { get; set; } = null!;

    public required string Summary { get; set; }
    public string? SentimentJson { get; set; }
    public string? RubricResultsJson { get; set; }
    public string? NextBestAction { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
