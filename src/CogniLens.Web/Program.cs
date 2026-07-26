using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CogniLens.Web;
using CogniLens.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("ApiBaseUrl is not configured (see wwwroot/appsettings.json).");

// The committed wwwroot/appsettings.json holds a placeholder; cd.yml overwrites it with the real
// Api URL before `dotnet publish`. Null is not the only failure mode — a skipped injection step
// leaves a perfectly well-formed value that happens to point nowhere, and without this check the
// app would start cleanly and then fail every request with a DNS error. cd.yml also greps the
// published bundle for the placeholder, so the normal path catches this before anything ships;
// this is the backstop for a manual or local publish.
if (apiBaseUrl.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"ApiBaseUrl is still the placeholder '{apiBaseUrl}' — this bundle was published without the deploy-time configuration step.");
}

builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddTransient<AuthHeaderHandler>();

builder.Services.AddHttpClient<CogniLensApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<AuthHeaderHandler>();

// Direct-to-blob upload via the SAS URL the API hands back — a plain client with no base
// address and no API key attached, since Azure Blob Storage authenticates the SAS token itself.
builder.Services.AddHttpClient("Blob");

await builder.Build().RunAsync();
