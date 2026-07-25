using CogniLens.Core.Enums;

namespace CogniLens.Core.Entities;

public class Call
{
    public Guid Id { get; set; }
    public required string BlobUri { get; set; }
    public required string OriginalFileName { get; set; }
    public CallStatus Status { get; set; } = CallStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    public ICollection<TranscriptSegment> TranscriptSegments { get; set; } = new List<TranscriptSegment>();
    public QaReport? QaReport { get; set; }
    public ICollection<JobStatus> JobStatuses { get; set; } = new List<JobStatus>();
}
