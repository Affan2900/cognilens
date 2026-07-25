using CogniLens.Core.Contracts;

namespace CogniLens.Core.Chunking;

// Chunk size is sized against the AI Search Free tier's ~10K-document ceiling: at ~1200
// chars/chunk with 150-char overlap, a typical 10-minute call (~8-10K transcript chars)
// produces ~8-10 chunks, so the demo corpus (dozens of calls) stays orders of magnitude
// under the ceiling.
public static class TranscriptChunker
{
    private const int ChunkSizeChars = 1200;
    private const int OverlapChars = 150;

    public static IReadOnlyList<TranscriptChunk> Chunk(IReadOnlyList<TranscribedSegment> segments)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        var fullText = string.Join(
            '\n',
            segments.OrderBy(s => s.SequenceNumber).Select(s => $"[Speaker {s.SpeakerTag}] {s.Text}"));

        var chunks = new List<TranscriptChunk>();
        var index = 0;
        var start = 0;

        while (start < fullText.Length)
        {
            var length = Math.Min(ChunkSizeChars, fullText.Length - start);
            var end = start + length;

            if (end < fullText.Length)
            {
                var lastBreak = fullText.LastIndexOf('\n', end - 1, length);
                if (lastBreak > start)
                {
                    end = lastBreak + 1;
                }
            }

            var text = fullText[start..end].Trim();
            if (text.Length > 0)
            {
                chunks.Add(new TranscriptChunk(index++, text));
            }

            if (end >= fullText.Length)
            {
                break;
            }

            start = Math.Max(end - OverlapChars, start + 1);
        }

        return chunks;
    }
}

public record TranscriptChunk(int Index, string Text);
