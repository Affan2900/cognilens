using CogniLens.Core.Enums;

namespace CogniLens.Core.Entities;

public class JobStatus
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public Call Call { get; set; } = null!;

    public required string MessageId { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
