namespace CogniLens.Core.Contracts;

public interface ITranscriptionService
{
    Task<IReadOnlyList<TranscribedSegment>> TranscribeAsync(string blobUri, CancellationToken cancellationToken);
}

public record TranscribedSegment(int SequenceNumber, int SpeakerTag, string Text, TimeSpan StartTime, TimeSpan EndTime);
