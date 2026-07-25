namespace CogniLens.Core.Contracts;

public interface IJobQueue
{
    Task<string> EnqueueAnalyzeJobAsync(Guid callId, CancellationToken cancellationToken);
}
