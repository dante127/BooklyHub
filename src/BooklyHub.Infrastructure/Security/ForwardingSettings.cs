using System.Net;
using Microsoft.Extensions.Configuration;

namespace BooklyHub.Infrastructure.Security;

/// <summary>
/// Which addresses this deployment sits behind. A host that terminates TLS in a proxy or load balancer sees
/// every request arrive from that one machine, so the address the limiter partitions on is the proxy's, and all
/// traffic shares one budget: measured, eleven failed sign-ins from eleven different callers locked the door for
/// everyone, because <c>RemoteIpAddress</c> was the same string for all of them. Trusting the forwarded header the
/// proxy writes is the fix, and trusting it is only safe for a peer the operator has named — any caller can write
/// that header, so an unnamed trust lets a guilty party choose the address it is bucketed under. So the list is
/// required to be explicit, and an empty list means no header is read at all, which is the behaviour before this
/// class existed.
/// </summary>
public sealed record ForwardingSettings(IReadOnlyList<IPAddress> KnownProxies, IReadOnlyList<IPNetwork> KnownNetworks)
{
    public const string KnownProxiesKey = "Forwarding:KnownProxies";
    public const string KnownNetworksKey = "Forwarding:KnownNetworks";

    /// <summary>Nothing declared, so nothing is trusted and the host keeps the address the TCP connection gives it.</summary>
    public bool IsConfigured => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    public static ForwardingSettings FromConfiguration(IConfiguration configuration) =>
        new(ReadProxies(configuration), ReadNetworks(configuration));

    /// <summary>
    /// A mistyped entry is refused at startup rather than dropped. A deployment that believes it trusts its proxy
    /// and silently does not is worse than one that will not boot: the shared-bucket behaviour the operator
    /// configured this away keeps serving, and nothing says so.
    /// </summary>
    private static IReadOnlyList<IPAddress> ReadProxies(IConfiguration configuration)
    {
        var proxies = new List<IPAddress>();

        foreach (var entry in ReadList(configuration, KnownProxiesKey))
        {
            if (!IPAddress.TryParse(entry, out var address))
            {
                throw Malformed(entry, KnownProxiesKey, "an IP address");
            }

            proxies.Add(address);
        }

        return proxies;
    }

    private static IReadOnlyList<IPNetwork> ReadNetworks(IConfiguration configuration)
    {
        var networks = new List<IPNetwork>();

        foreach (var entry in ReadList(configuration, KnownNetworksKey))
        {
            if (!IPNetwork.TryParse(entry, out var network))
            {
                throw Malformed(entry, KnownNetworksKey, "a CIDR block such as 10.0.0.0/8");
            }

            networks.Add(network);
        }

        return networks;
    }

    private static InvalidOperationException Malformed(string entry, string key, string expectedShape) => new(
        $"Configuration key '{key}' contains '{entry}', which is not {expectedShape}. " +
        "Host names are not accepted: a proxy must be named by the address its connections actually arrive from.");

    /// <summary>
    /// Reads both shapes an operator realistically writes: one environment variable holding a comma-separated
    /// list, or the indexed form the configuration system turns into a section.
    /// </summary>
    private static IReadOnlyList<string> ReadList(IConfiguration configuration, string key)
    {
        var values = new List<string>();

        if (configuration[key] is { } inline)
        {
            values.Add(inline);
        }

        foreach (var child in configuration.GetSection(key).GetChildren())
        {
            if (child.Value is { } element)
            {
                values.Add(element);
            }
        }

        return values
            .SelectMany(value => value.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .ToList();
    }
}
