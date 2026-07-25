using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CogniLens.Web;
using CogniLens.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("ApiBaseUrl is not configured (see wwwroot/appsettings.json).");

builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddTransient<AuthHeaderHandler>();

builder.Services.AddHttpClient<CogniLensApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<AuthHeaderHandler>();

// Direct-to-blob upload via the SAS URL the API hands back — a plain client with no base
// address and no API key attached, since Azure Blob Storage authenticates the SAS token itself.
builder.Services.AddHttpClient("Blob");

await builder.Build().RunAsync();
