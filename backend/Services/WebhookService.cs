using System.Text;
using System.Text.Json;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Fires generic webhooks for streaming events.
/// Supports multiple endpoints with configurable URL, method, and headers.
/// </summary>
public class WebhookService
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;

    public WebhookService(ConfigManager configManager)
    {
        _configManager = configManager;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Fire webhooks for a streaming event.
    /// </summary>
    public async Task FireEventAsync(string eventName, object payload, CancellationToken ct = default)
    {
        var config = _configManager.GetWebhookConfig();

        if (!config.Enabled || config.Endpoints.Count == 0)
        {
            Log.Debug("[Webhook] Webhooks disabled or no endpoints configured");
            return;
        }

        var matchingEndpoints = config.Endpoints
            .Where(e => e.Enabled && e.Events.Contains(eventName))
            .ToList();

        if (matchingEndpoints.Count == 0)
        {
            Log.Debug("[Webhook] No endpoints subscribed to event: {Event}", eventName);
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            @event = eventName,
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            data = payload
        });

        foreach (var endpoint in matchingEndpoints)
        {
            _ = FireWebhookAsync(endpoint, json, ct);
        }
    }

    private async Task FireWebhookAsync(WebhookEndpoint endpoint, string json, CancellationToken ct)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(
                    new HttpMethod(endpoint.Method.ToUpper()),
                    endpoint.Url
                );

                // Add custom headers
                foreach (var header in endpoint.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                // Set content for POST/PUT
                if (endpoint.Method.ToUpper() is "POST" or "PUT")
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    Log.Debug("[Webhook] Successfully fired webhook to {Name}", endpoint.Name);
                    return;
                }

                Log.Warning("[Webhook] Webhook {Name} returned {Status}",
                    endpoint.Name, response.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Warning("[Webhook] Attempt {Attempt}/{Max} failed for {Name}: {Error}",
                    attempt, maxRetries, endpoint.Name, ex.Message);

                if (attempt < maxRetries)
                {
                    // Exponential backoff: 1s, 2s, 4s
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
                }
            }
        }

        Log.Error("[Webhook] All attempts failed for {Name}", endpoint.Name);
    }

    /// <summary>
    /// Test a webhook endpoint by firing a test event.
    /// </summary>
    public async Task<WebhookTestResult> TestEndpointAsync(WebhookEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                @event = "test",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { message = "Test webhook from NZBDav" }
            });

            var request = new HttpRequestMessage(
                new HttpMethod(endpoint.Method.ToUpper()),
                endpoint.Url
            );

            foreach (var header in endpoint.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (endpoint.Method.ToUpper() is "POST" or "PUT")
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request, ct);

            return new WebhookTestResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Message = response.IsSuccessStatusCode
                    ? "Webhook delivered successfully"
                    : $"Webhook returned {response.StatusCode}"
            };
        }
        catch (HttpRequestException ex)
        {
            return new WebhookTestResult
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new WebhookTestResult
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }
}

public record WebhookTestResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string Message { get; init; } = "";
}
