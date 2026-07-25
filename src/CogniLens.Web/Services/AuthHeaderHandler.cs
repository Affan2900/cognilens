namespace CogniLens.Web.Services;

public class AuthHeaderHandler(ApiKeyStore apiKeyStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = await apiKeyStore.GetKeyAsync();
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Remove("X-Api-Key");
            request.Headers.Add("X-Api-Key", key);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
