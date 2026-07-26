using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using CogniLens.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CogniLens.UnitTests;

/// <summary>
/// Guards the storage client wiring against the failure that shipped to dev in Phase 3 and went
/// unnoticed until Phase 5: appsettings.json carried "Storage:ConnectionString":
/// "UseDevelopmentStorage=true", aca.bicep set Storage__QueueServiceUri but never cleared the
/// connection string, and the connection-string branch won. The deployed Worker spent every run
/// dialling the Azurite emulator on 127.0.0.1:10001 while the correct URI sat in configuration
/// being ignored. Nothing failed at startup, so nothing surfaced it.
/// </summary>
public class StorageClientConfigurationTests
{
    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddCogniLensInfrastructure(configuration)
            .BuildServiceProvider();
    }

    [Fact]
    public void Both_a_connection_string_and_a_service_uri_is_rejected_rather_than_silently_resolved()
    {
        using var provider = BuildProvider(
            ("Storage:ConnectionString", "UseDevelopmentStorage=true"),
            ("Storage:QueueServiceUri", "https://cognilensdevst.queue.core.windows.net/"));

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<QueueServiceClient>());

        Assert.Contains("mutually exclusive", ex.Message);
        // The account key in a real connection string must never reach a log or an error page.
        Assert.DoesNotContain("UseDevelopmentStorage", ex.Message);
    }

    [Fact]
    public void Neither_a_connection_string_nor_a_service_uri_fails_at_startup_instead_of_at_first_use()
    {
        using var provider = BuildProvider(("Storage:ContainerName", "call-audio"));

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<BlobServiceClient>());

        Assert.Contains("neither", ex.Message);
    }

    [Fact]
    public void A_service_uri_alone_builds_a_managed_identity_client()
    {
        using var provider = BuildProvider(
            ("Storage:BlobServiceUri", "https://cognilensdevst.blob.core.windows.net/"),
            ("Storage:QueueServiceUri", "https://cognilensdevst.queue.core.windows.net/"));

        Assert.Equal("cognilensdevst", provider.GetRequiredService<BlobServiceClient>().AccountName);
        Assert.Equal("cognilensdevst", provider.GetRequiredService<QueueServiceClient>().AccountName);
    }

    [Fact]
    public void A_connection_string_alone_still_builds_the_emulator_client_for_local_development()
    {
        using var provider = BuildProvider(("Storage:ConnectionString", "UseDevelopmentStorage=true"));

        Assert.Equal("devstoreaccount1", provider.GetRequiredService<QueueServiceClient>().AccountName);
    }
}
