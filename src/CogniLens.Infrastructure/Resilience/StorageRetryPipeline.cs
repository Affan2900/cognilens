using Polly;
using Polly.Retry;

namespace CogniLens.Infrastructure.Resilience;

// Shared retry policy for outbound Azure Storage calls (blob SAS issuance, queue send/receive/delete).
// Azure OpenAI/Speech/Search calls get their own pipelines when Phase 2 introduces them.
public static class StorageRetryPipeline
{
    public static readonly ResiliencePipeline Instance = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .Build();
}
