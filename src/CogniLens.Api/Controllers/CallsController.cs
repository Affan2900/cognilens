using CogniLens.Core.Contracts;
using CogniLens.Core.Dtos;
using CogniLens.Core.Entities;
using CogniLens.Core.Enums;
using CogniLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CogniLens.Api.Controllers;

[ApiController]
[Route("api/calls")]
public class CallsController(
    CogniLensDbContext db,
    IBlobStorageService blobStorageService,
    IJobQueue jobQueue) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<CreateCallResponse>> CreateCall(
        [FromBody] CreateCallRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalFileName))
        {
            return ValidationProblem("OriginalFileName is required.");
        }

        var call = new Call
        {
            Id = Guid.NewGuid(),
            OriginalFileName = request.OriginalFileName,
            BlobUri = string.Empty,
            Status = CallStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var ticket = await blobStorageService.CreateUploadTicketAsync(call.Id, request.OriginalFileName, cancellationToken);
        call.BlobUri = ticket.BlobUri;

        db.Calls.Add(call);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetCall), new { id = call.Id },
            new CreateCallResponse(call.Id, ticket.UploadUrl, ticket.BlobUri, ticket.ExpiresAt));
    }

    [HttpPost("{id:guid}/analyze")]
    [EnableRateLimiting("ai-cost-guardrail")]
    public async Task<ActionResult<AnalyzeCallResponse>> AnalyzeCall(Guid id, CancellationToken cancellationToken)
    {
        var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (call is null)
        {
            return NotFound();
        }

        if (call.Status != CallStatus.Pending)
        {
            return Conflict($"Call {id} is already {call.Status}.");
        }

        var messageId = await jobQueue.EnqueueAnalyzeJobAsync(id, cancellationToken);

        db.JobStatuses.Add(new JobStatus
        {
            Id = Guid.NewGuid(),
            CallId = id,
            MessageId = messageId,
            State = JobState.Queued,
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        return Accepted(new AnalyzeCallResponse(id, call.Status));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CallStatusResponse>> GetCall(Guid id, CancellationToken cancellationToken)
    {
        var call = await db.Calls
            .Include(c => c.QaReport)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (call is null)
        {
            return NotFound();
        }

        var report = call.QaReport is null
            ? null
            : new QaReportDto(call.QaReport.Summary, call.QaReport.SentimentJson, call.QaReport.RubricResultsJson, call.QaReport.NextBestAction);

        return Ok(new CallStatusResponse(call.Id, call.Status, call.CreatedAt, call.ProcessedAt, report));
    }
}
