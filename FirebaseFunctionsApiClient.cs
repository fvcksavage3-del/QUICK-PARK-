using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickPark;

internal sealed class FirebaseFunctionsApiClient
{
    private const string Region = "us-central1";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly FirebaseAuthService _authService = new();

    public async Task<TResponse> CallAsync<TResponse>(
        string action,
        object data,
        CancellationToken cancellationToken = default)
    {
        var projectId = FirebaseProjectConfig.Load().ProjectId;
        var endpoint = new Uri(
            $"https://{Region}-{projectId}.cloudfunctions.net/adminApi");
        var idToken = await _authService.GetValidIdTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { action, data }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FirebaseFunctionNotDeployedException(
                    "The QuickPark admin API is not deployed or its URL is incorrect. Deploy the adminApi Firebase Function and try again.");
            }
            var message = ReadErrorMessage(body) ??
                $"QuickPark's Firebase Functions API returned HTTP {(int)response.StatusCode}.";
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new FirebaseAuthException(message);
            }
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                throw new InvalidOperationException(message);
            }
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(body, JsonOptions)
                ?? throw new InvalidOperationException("Firebase Functions returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Firebase Functions returned an unreadable response.",
                exception);
        }
    }

    private static string? ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

}

internal sealed class FirebaseFunctionNotDeployedException(string message) : InvalidOperationException(message);
