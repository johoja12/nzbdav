using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers;

/// <summary>
/// API endpoint to get current health status of all integrations.
/// </summary>
[ApiController]
[Route("api/server-health")]
public class ServerHealthController : BaseApiController
{
    private readonly ConfigManager _configManager;
    private readonly HttpClient _httpClient;

    public ServerHealthController(ConfigManager configManager, IHttpClientFactory httpClientFactory)
    {
        _configManager = configManager;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        // Get Plex/Emby health from verification services
        var plexHealth = PlexVerificationService.Instance?.GetServerHealthStatus()
            ?? new Dictionary<string, ServerHealth>();
        var embyHealth = EmbyVerificationService.Instance?.GetServerHealthStatus()
            ?? new Dictionary<string, ServerHealth>();

        // Get SABnzbd health
        var sabHealth = await GetSabHealthAsync();

        // Get Radarr/Sonarr health
        var radarrHealth = await GetArrHealthAsync("radarr");
        var sonarrHealth = await GetArrHealthAsync("sonarr");

        // Get Rclone health from database
        var rcloneHealth = await GetRcloneHealthAsync();

        return Ok(new IntegrationHealthResponse
        {
            Status = true,
            Plex = plexHealth.ToDictionary(kvp => kvp.Key, kvp => ToDto(kvp.Value)),
            Emby = embyHealth.ToDictionary(kvp => kvp.Key, kvp => ToDto(kvp.Value)),
            Sabnzbd = sabHealth,
            Radarr = radarrHealth,
            Sonarr = sonarrHealth,
            Rclone = rcloneHealth
        });
    }

    private static ServerHealthDto ToDto(ServerHealth health) => new()
    {
        ServerName = health.ServerName,
        ServerType = health.ServerType,
        IsReachable = health.IsReachable,
        LastChecked = health.LastChecked,
        LastReachable = health.LastReachable,
        LastError = health.LastError,
        ConsecutiveFailures = health.ConsecutiveFailures
    };

    private async Task<Dictionary<string, ServerHealthDto>> GetSabHealthAsync()
    {
        var result = new Dictionary<string, ServerHealthDto>();
        var config = _configManager.GetSabPauseConfig();

        var servers = config.Servers.Where(s => s.Enabled).ToList();

        // Fall back to legacy single-server config
        if (servers.Count == 0 && !string.IsNullOrEmpty(config.Url) && !string.IsNullOrEmpty(config.ApiKey))
        {
            servers.Add(new SabServer { Name = "SABnzbd", Url = config.Url, ApiKey = config.ApiKey, Enabled = true });
        }

        foreach (var server in servers)
        {
            var health = new ServerHealthDto
            {
                ServerName = server.Name,
                ServerType = "sabnzbd",
                LastChecked = DateTime.UtcNow
            };

            try
            {
                var url = $"{server.Url.TrimEnd('/')}/api?mode=queue&output=json&apikey={server.ApiKey}";
                var response = await _httpClient.GetAsync(url);
                health.IsReachable = response.IsSuccessStatusCode;
                if (health.IsReachable)
                {
                    health.LastReachable = DateTime.UtcNow;
                }
                else
                {
                    health.LastError = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                health.IsReachable = false;
                health.LastError = ex.Message;
            }

            result[server.Name] = health;
        }

        return result;
    }

    private async Task<Dictionary<string, ServerHealthDto>> GetArrHealthAsync(string arrType)
    {
        var result = new Dictionary<string, ServerHealthDto>();
        var arrConfig = _configManager.GetArrConfig();
        var instances = arrType == "radarr" ? arrConfig.RadarrInstances : arrConfig.SonarrInstances;

        foreach (var instance in instances)
        {
            var name = GetArrDisplayName(instance.Host, arrType);
            var health = new ServerHealthDto
            {
                ServerName = name,
                ServerType = arrType,
                LastChecked = DateTime.UtcNow
            };

            try
            {
                var url = $"{instance.Host.TrimEnd('/')}/api/v3/system/status?apikey={instance.ApiKey}";
                var response = await _httpClient.GetAsync(url);
                health.IsReachable = response.IsSuccessStatusCode;
                if (health.IsReachable)
                {
                    health.LastReachable = DateTime.UtcNow;
                }
                else
                {
                    health.LastError = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                health.IsReachable = false;
                health.LastError = ex.Message;
            }

            result[name] = health;
        }

        return result;
    }

    private static string GetArrDisplayName(string host, string arrType)
    {
        try
        {
            var uri = new Uri(host);
            var displayType = arrType == "radarr" ? "Radarr" : "Sonarr";
            return $"{displayType} ({uri.Host}:{uri.Port})";
        }
        catch
        {
            return host;
        }
    }

    private async Task<Dictionary<string, ServerHealthDto>> GetRcloneHealthAsync()
    {
        var result = new Dictionary<string, ServerHealthDto>();

        try
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var instances = await dbContext.RcloneInstances.ToListAsync();

            foreach (var instance in instances)
            {
                result[instance.Name] = new ServerHealthDto
                {
                    ServerName = instance.Name,
                    ServerType = "rclone",
                    IsReachable = instance.LastTestSuccess ?? false,
                    LastChecked = instance.LastTestedAt?.UtcDateTime ?? DateTime.MinValue,
                    LastReachable = instance.LastTestSuccess == true ? instance.LastTestedAt?.UtcDateTime : null,
                    LastError = instance.LastTestError,
                    ConsecutiveFailures = 0
                };
            }
        }
        catch
        {
            // Ignore - rclone instances table may not exist
        }

        return result;
    }
}

public class IntegrationHealthResponse : BaseApiResponse
{
    public Dictionary<string, ServerHealthDto> Plex { get; set; } = new();
    public Dictionary<string, ServerHealthDto> Emby { get; set; } = new();
    public Dictionary<string, ServerHealthDto> Sabnzbd { get; set; } = new();
    public Dictionary<string, ServerHealthDto> Radarr { get; set; } = new();
    public Dictionary<string, ServerHealthDto> Sonarr { get; set; } = new();
    public Dictionary<string, ServerHealthDto> Rclone { get; set; } = new();
}

public class ServerHealthDto
{
    public string ServerName { get; set; } = "";
    public string ServerType { get; set; } = "";
    public bool IsReachable { get; set; }
    public DateTime LastChecked { get; set; }
    public DateTime? LastReachable { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? Version { get; set; }
}
