using Microsoft.JSInterop;

namespace CogniLens.Web.Services;

// The API key lives in the browser's localStorage, entered once via the connect banner in
// MainLayout — never baked into wwwroot/appsettings.json, since that file ships inside the
// compiled WASM bundle and would mean committing the key to source control.
public class ApiKeyStore(IJSRuntime jsRuntime)
{
    private const string StorageKey = "cognilens-api-key";
    private string? _cachedKey;

    public event Action? Changed;

    public async Task<string?> GetKeyAsync()
    {
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }

        _cachedKey = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        return _cachedKey;
    }

    public async Task SetKeyAsync(string key)
    {
        _cachedKey = key;
        await jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, key);
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        _cachedKey = null;
        await jsRuntime.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        Changed?.Invoke();
    }
}
