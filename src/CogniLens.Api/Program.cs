using System.Threading.RateLimiting;
using CogniLens.Api;
using CogniLens.Infrastructure;
using CogniLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddCogniLensInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CogniLensDbContext>("database", tags: ["ready"]);

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
}

// TLS terminates at the Container Apps ingress in every real deployment, so the app
// itself never needs to redirect to https.
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
