using Polly;
using Polly.Retry;

namespace CogniLens.Infrastructure.Resilience;

// Separate from StorageRetryPipeline: AI calls (Speech polling, OpenAI, Search) are slower
// and more failure-prone (rate limits, transient 5xx) than blob/queue calls, so they get a
// longer backoff ceiling.
public static class AiRetryPipeline
{
    public static readonly ResiliencePipeline Instance = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 4,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .Build();
}
