namespace CogniLens.Core.Entities;

public class TranscriptSegment
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public Call Call { get; set; } = null!;

    public int SequenceNumber { get; set; }
    public int SpeakerTag { get; set; }
    public required string Text { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
}
