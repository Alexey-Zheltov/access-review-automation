using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AccessReviewPortal.Services.Azure;

public sealed class ArmRequestException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Thin REST client for ARM calls where the raw JSON matters (PIM expandedProperties, Activity Log,
/// ARM access reviews, permissions). Handles paging (nextLink) and throttling (429 / 5xx).
/// The default instance uses the scanner's application token (read-only); <see cref="WithUserToken"/> returns a
/// client that calls ARM with the signed-in user's delegated token.
/// </summary>
public sealed class ArmRestClient
{
    public const string BaseUrl = "https://management.azure.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly ILogger _logger;

    public ArmRestClient(IHttpClientFactory httpClientFactory, AzureClients clients, ILogger<ArmRestClient> logger)
        : this(httpClientFactory, async ct => (await clients.GetTokenAsync(AzureClients.ArmScope, ct)).Token, logger)
    {
    }

    private ArmRestClient(IHttpClientFactory httpClientFactory, Func<CancellationToken, Task<string>> tokenProvider, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    /// <summary>Same client, but every request carries the token returned by <paramref name="tokenProvider"/>.</summary>
    public ArmRestClient WithUserToken(Func<CancellationToken, Task<string>> tokenProvider) => new(_httpClientFactory, tokenProvider, _logger);

    public async Task<JsonElement> GetJsonAsync(string pathOrUrl, CancellationToken ct, int maxServerErrorRetries = 2)
    {
        using var response = await SendAsync(HttpMethod.Get, pathOrUrl, null, ct, maxServerErrorRetries);
        return await ReadJsonAsync(response, ct);
    }

    public async Task<JsonElement> PutJsonAsync(string pathOrUrl, object body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, pathOrUrl, body, ct, 0);
        return await ReadJsonAsync(response, ct);
    }

    public async Task DeleteAsync(string pathOrUrl, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, pathOrUrl, null, ct, 0);
        await ReadJsonAsync(response, ct);
    }

    /// <summary>Enumerates "value" items of a list response and follows nextLink.</summary>
    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string pathOrUrl, [EnumeratorCancellation] CancellationToken ct, int maxServerErrorRetries = 2)
    {
        string? next = pathOrUrl;
        while (!string.IsNullOrEmpty(next))
        {
            var page = await GetJsonAsync(next, ct, maxServerErrorRetries);
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    yield return item;
                }
            }
            next = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString()
                : null;
        }
    }

    public async Task<List<JsonElement>> GetAllAsync(string pathOrUrl, CancellationToken ct, int maxServerErrorRetries = 2)
    {
        var list = new List<JsonElement>();
        await foreach (var item in GetPagedAsync(pathOrUrl, ct, maxServerErrorRetries))
        {
            list.Add(item);
        }
        return list;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, object? body, CancellationToken ct, int maxServerErrorRetries)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : BaseUrl + pathOrUrl;
        var client = _httpClientFactory.CreateClient("arm");
        var throttleRetries = 0;
        var serverErrorRetries = 0;

        while (true)
        {
            var token = await _tokenProvider(ct);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout, not a cancellation of the scan.
                if (++serverErrorRetries > Math.Max(1, maxServerErrorRetries))
                {
                    throw new ArmRequestException(HttpStatusCode.RequestTimeout, $"ARM request timed out: {url.Split('?')[0]}");
                }
                continue;
            }
            var status = (int)response.StatusCode;

            var retry = status == 429 ? ++throttleRetries <= 6 : status >= 500 && ++serverErrorRetries <= maxServerErrorRetries;
            if (!retry)
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, throttleRetries + serverErrorRetries)));
            _logger.LogDebug("ARM {Status} for {Url}. Retry in {Delay}.", status, url, delay);
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ArmRequestException(response.StatusCode, $"ARM {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(ExtractError(text), 400)}");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string ExtractError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                return $"{code}: {msg}";
            }
            if (doc.RootElement.TryGetProperty("Message", out var m2))
            {
                return m2.GetString() ?? text;
            }
        }
        catch (JsonException)
        {
        }
        return text;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
