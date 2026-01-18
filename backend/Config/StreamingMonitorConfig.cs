using System.Text.Json.Serialization;

namespace NzbWebDAV.Config;

public record StreamingMonitorConfig
{
    public bool Enabled { get; init; }
    public int StartDebounceSeconds { get; init; } = 2;
    public int StopDebounceSeconds { get; init; } = 5;
}

public record PlexServer
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public record PlexConfig
{
    public bool VerifyPlayback { get; init; } = true;
    public List<PlexServer> Servers { get; init; } = new();
}

// Support for multiple SABnzbd servers
public record SabServer
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public record SabPauseConfig
{
    public bool AutoPause { get; init; } = true;
    public List<SabServer> Servers { get; init; } = new();

    // Legacy single-server support (for migration)
    public string Url { get; init; } = "";
    public string ApiKey { get; init; } = "";
}

public record WebhookEndpoint
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Method { get; init; } = "POST";
    public Dictionary<string, string> Headers { get; init; } = new();
    public List<string> Events { get; init; } = new();
    public bool Enabled { get; init; } = true;
}

public record WebhookConfig
{
    public bool Enabled { get; init; }
    public List<WebhookEndpoint> Endpoints { get; init; } = new();
}
