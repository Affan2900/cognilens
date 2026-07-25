using CogniLens.Core.Dtos;
using CogniLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CogniLens.Api.Controllers;

[ApiController]
[Route("api/rubrics")]
public class RubricsController(CogniLensDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RubricDto>>> GetRubrics(CancellationToken cancellationToken)
    {
        var rubrics = await db.Rubrics
            .Where(r => r.IsActive)
            .OrderBy(r => r.Category)
            .ThenBy(r => r.Name)
            .Select(r => new RubricDto(r.Id, r.Name, r.Description, r.Category))
            .ToListAsync(cancellationToken);

        return Ok(rubrics);
    }
}
