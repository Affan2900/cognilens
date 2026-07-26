using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CogniLens.Api.Controllers;

[ApiController]
[Route("api/search")]
public class SearchController(ISearchIndexService searchIndexService) : ControllerBase
{
    private const int DefaultTop = 10;
    private const int MaxTop = 50;

    // The body-size cap in Program.cs does not cover this endpoint: `q` arrives in the query
    // string, which Kestrel limits separately (and far more generously). A long query is billed
    // work — it is embedded and sent to Azure AI Search — so it needs its own ceiling. 1000
    // characters is well past any real search phrase.
    private const int MaxQueryLength = 1000;

    [HttpGet]
    [EnableRateLimiting("ai-cost-guardrail")]
    public async Task<ActionResult<IReadOnlyList<SearchResultDto>>> Search(
        [FromQuery] string q, [FromQuery] int top, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return ValidationProblem("q is required.");
        }

        if (q.Length > MaxQueryLength)
        {
            return ValidationProblem($"q must be {MaxQueryLength} characters or fewer.");
        }

        var take = top <= 0 ? DefaultTop : Math.Min(top, MaxTop);

        var results = await searchIndexService.SearchAsync(q, take, cancellationToken);

        return Ok(results
            .Select(r => new SearchResultDto(r.CallId, r.OriginalFileName, r.ChunkIndex, r.Text, r.Score))
            .ToList());
    }
}
