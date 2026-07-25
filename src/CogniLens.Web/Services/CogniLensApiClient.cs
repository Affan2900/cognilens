using System.Net.Http.Json;
using CogniLens.Core.Dtos;

namespace CogniLens.Web.Services;

public class CogniLensApiClient(HttpClient http)
{
    public async Task<CreateCallResponse> CreateCallAsync(string fileName, CancellationToken cancellationToken)
    {
        var response = await http.PostAsJsonAsync("api/calls", new CreateCallRequest(fileName), cancellationToken);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreateCallResponse>(cancellationToken))!;
    }

    public async Task AnalyzeCallAsync(Guid callId, CancellationToken cancellationToken)
    {
        var response = await http.PostAsync($"api/calls/{callId}/analyze", content: null, cancellationToken);
        await EnsureSuccessAsync(response);
    }

    public async Task<CallStatusResponse> GetCallAsync(Guid callId, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"api/calls/{callId}", cancellationToken);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CallStatusResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<RubricDto>> GetRubricsAsync(CancellationToken cancellationToken)
    {
        var response = await http.GetAsync("api/rubrics", cancellationToken);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<RubricDto>>(cancellationToken))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var message = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "API key was rejected. Check the key and try again.",
            System.Net.HttpStatusCode.TooManyRequests => "Rate limit hit — wait a minute and try again.",
            _ => string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "Request failed." : body
        };
        throw new ApiException((int)response.StatusCode, message);
    }
}

public class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
