using System.Threading.RateLimiting;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CogniLens.Api;
using CogniLens.Infrastructure;
using CogniLens.Infrastructure.Observability;
using CogniLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddCogniLensObservability("cognilens-api");

// Every endpoint on this API takes either a query string or a small JSON body — audio never
// passes through here, since clients PUT it straight to a blob SAS URL. Kestrel's 30 MB default
// is therefore ~240x larger than anything legitimate, and each oversized body is memory an
// unauthenticated caller can make the process allocate before ApiKeyMiddleware ever runs.
// Kestrel rejects over-limit requests with 413 before the body is buffered.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddCogniLensInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CogniLensDbContext>("database", tags: ["ready"]);

// The Blazor WASM frontend is a separate origin (its own dev port locally, its own Container
// App/static host in Azure), so it needs an explicit CORS grant to call this API. X-Api-Key
// has to be listed explicitly since AllowAnyHeader() doesn't cover it once credentials-adjacent
// headers are involved in some browsers — listing it is the safe, explicit choice either way.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyMethod()
        .WithHeaders("Content-Type", "X-Api-Key", TraceCorrelationMiddleware.HeaderName)
        // Without this the browser can see the response but not read X-Correlation-Id off it,
        // which defeats the entire point of returning it — the frontend is the one place a user
        // could actually be shown the id to quote in a bug report.
        .WithExposedHeaders(TraceCorrelationMiddleware.HeaderName));
});

// Applied to /analyze and /api/search specifically (see [EnableRateLimiting] on those actions) —
// both trigger paid Azure AI calls, so they're the ones that need a cap before the API is public.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ai-cost-guardrail", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Request.Headers.TryGetValue("X-Api-Key", out var key) ? key.ToString() : "anonymous",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<CogniLensDbContext>().Database.Migrate();

    // Azurite enforces the same CORS rules as real Azure Storage, so a browser PUT straight to
    // a blob SAS URL (the Blazor upload flow) is blocked until the account has a CORS rule. Real
    // Azure Storage gets its rule from storage.bicep; Azurite has no such provisioning step, so
    // it's set here instead, once per Api start — idempotent, dev-only.
    var devBlobServiceClient = scope.ServiceProvider.GetRequiredService<BlobServiceClient>();
    BlobServiceProperties devBlobProperties = await devBlobServiceClient.GetPropertiesAsync();
    devBlobProperties.Cors = new List<BlobCorsRule>
    {
        new()
        {
            AllowedOrigins = "*",
            AllowedMethods = "GET,PUT,OPTIONS",
            AllowedHeaders = "*",
            ExposedHeaders = "*",
            MaxAgeInSeconds = 3600
        }
    };
    await devBlobServiceClient.SetPropertiesAsync(devBlobProperties);
}

// First in the pipeline so even a 401 from ApiKeyMiddleware or a CORS rejection comes back with
// a correlation id — those are exactly the responses someone needs help diagnosing.
app.UseMiddleware<TraceCorrelationMiddleware>();

// TLS terminates at the Container Apps ingress in every real deployment, so the app
// itself never needs to redirect to https.
app.UseCors("Web");

app.UseAuthorization();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseMiddleware<ApiKeyMiddleware>());

app.UseRateLimiter();

app.MapControllers();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
