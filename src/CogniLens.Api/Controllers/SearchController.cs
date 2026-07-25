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

    [HttpGet]
    [EnableRateLimiting("ai-cost-guardrail")]
    public async Task<ActionResult<IReadOnlyList<SearchResultDto>>> Search(
        [FromQuery] string q, [FromQuery] int top, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return ValidationProblem("q is required.");
        }

        var take = top <= 0 ? DefaultTop : Math.Min(top, MaxTop);

        var results = await searchIndexService.SearchAsync(q, take, cancellationToken);

        return Ok(results
            .Select(r => new SearchResultDto(r.CallId, r.OriginalFileName, r.ChunkIndex, r.Text, r.Score))
            .ToList());
    }
}
