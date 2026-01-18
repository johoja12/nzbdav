using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace NzbWebDAV.Api.Controllers.PlexAuth;

[ApiController]
[Route("api/plex-auth")]
public class PlexAuthController : ControllerBase
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string PLEX_API_PINS = "https://plex.tv/api/v2/pins";
    private const string PLEX_API_RESOURCES = "https://plex.tv/api/v2/resources";
    private const string PRODUCT_NAME = "NzbDav";

    // Use a consistent client identifier per installation
    private static readonly string ClientId = Guid.NewGuid().ToString();

    /// <summary>
    /// Create a Plex PIN for authentication.
    /// Returns the PIN ID, code, and auth URL for the user to open.
    /// </summary>
    [HttpPost("pin")]
    public async Task<IActionResult> CreatePin()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, PLEX_API_PINS);
            request.Headers.Add("X-Plex-Product", PRODUCT_NAME);
            request.Headers.Add("X-Plex-Client-Identifier", ClientId);
            request.Headers.Add("Accept", "application/json");
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("strong", "true") });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<PlexPinResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null)
                return StatusCode(500, new { error = "Failed to parse Plex PIN response" });

            // Construct the auth URL
            var authUrl = $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(ClientId)}&code={Uri.EscapeDataString(data.Code)}&context[device][product]={Uri.EscapeDataString(PRODUCT_NAME)}";

            Log.Information("[PlexAuth] Created PIN {PinId} with code {Code}", data.Id, data.Code);

            return Ok(new PlexPinResult
            {
                Id = data.Id,
                Code = data.Code,
                Url = authUrl,
                ClientId = ClientId
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PlexAuth] Failed to create PIN");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Check the status of a Plex PIN to see if the user has authenticated.
    /// Returns the auth token once authenticated, null otherwise.
    /// </summary>
    [HttpGet("pin/{pinId}")]
    public async Task<IActionResult> CheckPin(int pinId, [FromQuery] string clientId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{PLEX_API_PINS}/{pinId}");
            request.Headers.Add("X-Plex-Product", PRODUCT_NAME);
            request.Headers.Add("X-Plex-Client-Identifier", clientId ?? ClientId);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<PlexPinResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return Ok(new { authToken = data?.AuthToken });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PlexAuth] Failed to check PIN {PinId}", pinId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Fetch the list of Plex servers associated with the authenticated user.
    /// </summary>
    [HttpGet("servers")]
    public async Task<IActionResult> GetServers([FromQuery] string token, [FromQuery] string? clientId)
    {
        if (string.IsNullOrEmpty(token))
            return BadRequest(new { error = "Token is required" });

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{PLEX_API_RESOURCES}?includeHttps=1");
            request.Headers.Add("X-Plex-Token", token);
            request.Headers.Add("X-Plex-Client-Identifier", clientId ?? ClientId);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var devices = JsonSerializer.Deserialize<List<PlexDevice>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (devices == null)
                return Ok(new { servers = Array.Empty<object>() });

            // Filter for servers only
            var servers = devices
                .Where(d =>
                    (d.Roles != null && d.Roles.Contains("server")) ||
                    (d.Provides != null && d.Provides.Split(',').Contains("server")))
                .Select(d => new PlexServerInfo
                {
                    Name = d.Name ?? "Unknown",
                    ClientIdentifier = d.ClientIdentifier ?? "",
                    AccessToken = d.AccessToken ?? token, // Use device token if available, otherwise user token
                    Connections = d.Connections?.Select(c => new PlexConnectionInfo
                    {
                        Uri = c.Uri ?? "",
                        Local = c.Local,
                        Relay = c.Relay
                    }).ToList() ?? new List<PlexConnectionInfo>()
                })
                .ToList();

            Log.Information("[PlexAuth] Found {Count} Plex servers for token {TokenPrefix}...",
                servers.Count, token.Length > 8 ? token[..8] : token);

            return Ok(new { servers });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PlexAuth] Failed to fetch servers");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test connection to a specific Plex server URL with token.
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection([FromBody] PlexTestConnectionRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || string.IsNullOrEmpty(req.Token))
            return BadRequest(new { error = "URL and Token are required" });

        try
        {
            var url = req.Url.TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/identity");
            request.Headers.Add("X-Plex-Token", req.Token);
            request.Headers.Add("Accept", "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var identity = JsonSerializer.Deserialize<PlexIdentityResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return Ok(new
            {
                success = true,
                machineIdentifier = identity?.MediaContainer?.MachineIdentifier,
                version = identity?.MediaContainer?.Version
            });
        }
        catch (Exception ex)
        {
            Log.Warning("[PlexAuth] Connection test failed for {Url}: {Error}", req.Url, ex.Message);
            return Ok(new { success = false, error = ex.Message });
        }
    }
}

// Request/Response models
public class PlexPinResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string? AuthToken { get; set; }
}

public class PlexPinResult
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Url { get; set; } = "";
    public string ClientId { get; set; } = "";
}

public class PlexDevice
{
    public string? Name { get; set; }
    public string? ClientIdentifier { get; set; }
    public string? Provides { get; set; }
    public List<string>? Roles { get; set; }
    public string? AccessToken { get; set; }
    public List<PlexConnection>? Connections { get; set; }
}

public class PlexConnection
{
    public string? Uri { get; set; }
    public bool Local { get; set; }
    public bool Relay { get; set; }
}

public class PlexServerInfo
{
    public string Name { get; set; } = "";
    public string ClientIdentifier { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public List<PlexConnectionInfo> Connections { get; set; } = new();
}

public class PlexConnectionInfo
{
    public string Uri { get; set; } = "";
    public bool Local { get; set; }
    public bool Relay { get; set; }
}

public class PlexTestConnectionRequest
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
}

public class PlexIdentityResponse
{
    public PlexMediaContainer? MediaContainer { get; set; }
}

public class PlexMediaContainer
{
    public string? MachineIdentifier { get; set; }
    public string? Version { get; set; }
}
