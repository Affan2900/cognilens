using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CogniLens.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Wires traces, metrics and logs to Application Insights via the Azure Monitor OpenTelemetry
    /// distro. Safe to call when no connection string is configured (local dev) — instrumentation
    /// is still registered, it just has nowhere to export to.
    /// </summary>
    public static IHostApplicationBuilder AddCogniLensObservability(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        // aca.bicep has been setting APPLICATIONINSIGHTS_CONNECTION_STRING on both container apps
        // since Phase 2, but until now nothing read it. This is the line that makes it live.
        var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: serviceName,
                    serviceVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0",
                    // Container Apps runs multiple replicas behind one revision, so without this
                    // every replica's telemetry collapses into one indistinguishable stream and
                    // "is it one bad replica or the whole revision?" becomes unanswerable.
                    serviceInstanceId: Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
                        ?? Environment.MachineName)
                .AddAttributes(
                [
                    // The revision name carries the 7-char git SHA (cognilens-dev-api--86fbe15),
                    // which is what makes a span attributable to an exact commit — and what lets
                    // the canary window be sliced by revision to compare old vs new.
                    new KeyValuePair<string, object>(
                        "azure.containerapp.revision",
                        Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local")
                ]))
            .WithTracing(tracing => tracing
                .AddSource(CogniLensTelemetry.ActivitySourceName)
                // EF Core ships its own ActivitySource, so the SQL round-trips show up as child
                // spans without a separate instrumentation package.
                .AddSource("Microsoft.EntityFrameworkCore")
                // Blob, Queue, OpenAI and Search all go through Azure SDK clients that emit under
                // Azure.<Service>.*. This is what surfaces the actual cost-bearing AI calls —
                // transcription and completion latency — instead of one opaque gap in the trace.
                .AddSource("Azure.*"))
            .WithMetrics(metrics => metrics.AddMeter(CogniLensTelemetry.MeterName));

        // Trace/span correlation on log records is automatic — the OpenTelemetry logging provider
        // stamps them from Activity.Current. Scopes are not: they default to off, which would
        // silently drop the BeginScope dimensions the Worker attaches to every job's log lines,
        // and those are what make a log searchable by message id without a trace id in hand.
        builder.Services.Configure<OpenTelemetryLoggerOptions>(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
        });

        // Container Apps probes /healthz/live and /healthz/ready continuously on every replica,
        // and cd.yml's smoke test hammers /healthz/ready too. Left unfiltered those requests are
        // the overwhelming majority of spans — pure noise that also eats the App Insights free
        // 5 GB/month allowance for nothing.
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/healthz"));

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Deliberately not throwing: `dotnet run` locally has no App Insights resource, and
            // making observability a hard startup requirement would break local development for
            // no benefit. The instrumentation above is still registered, so ActivitySource spans
            // are still created — they just have no exporter attached.
            return builder;
        }

        telemetry.UseAzureMonitor(options => options.ConnectionString = connectionString);

        return builder;
    }
}
