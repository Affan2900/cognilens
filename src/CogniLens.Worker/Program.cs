using CogniLens.Infrastructure;
using CogniLens.Infrastructure.Observability;
using CogniLens.Infrastructure.Persistence;
using CogniLens.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddCogniLensObservability("cognilens-worker");

builder.Services.AddCogniLensInfrastructure(builder.Configuration);

builder.Services.AddHostedService<AnalyzeJobConsumer>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CogniLensDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
