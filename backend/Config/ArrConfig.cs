using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Config;

public class ArrConfig
{
    public List<ConnectionDetails> RadarrInstances { get; set; } = [];
    public List<ConnectionDetails> SonarrInstances { get; set; } = [];
    public List<QueueRule> QueueRules { get; set; } = [];

    /// <summary>
    /// Gets all Arr clients without path mappings (for operations that don't need path translation).
    /// </summary>
    // ReSharper disable once InvokeAsExtensionMethod
    public IEnumerable<ArrClient> GetArrClients() => Enumerable.Concat(
        RadarrInstances.Select(ArrClient (x) => new RadarrClient(x.Host, x.ApiKey)),
        SonarrInstances.Select(ArrClient (x) => new SonarrClient(x.Host, x.ApiKey))
    );

    /// <summary>
    /// Gets all Arr clients with path mappings applied (for operations that need path translation).
    /// </summary>
    /// <param name="pathMappingsResolver">Function to resolve path mappings by host URL</param>
    public IEnumerable<ArrClient> GetArrClients(Func<string, ArrPathMappings> pathMappingsResolver) => Enumerable.Concat(
        RadarrInstances.Select(x =>
        {
            var client = new RadarrClient(x.Host, x.ApiKey);
            client.PathMappings = pathMappingsResolver(x.Host);
            return (ArrClient)client;
        }),
        SonarrInstances.Select(x =>
        {
            var client = new SonarrClient(x.Host, x.ApiKey);
            client.PathMappings = pathMappingsResolver(x.Host);
            return (ArrClient)client;
        })
    );

    public int GetInstanceCount() =>
        RadarrInstances.Count + SonarrInstances.Count;

    public class ConnectionDetails
    {
        public required string Host { get; set; }
        public required string ApiKey { get; set; }
    }

    public class QueueRule
    {
        public string Message { get; set; } = null!;
        public QueueAction Action { get; set; }
    }

    public enum QueueAction
    {
        DoNothing = 0,
        Remove = 1,
        RemoveAndBlocklist = 2,
        RemoveAndBlocklistAndSearch = 3
    }
}