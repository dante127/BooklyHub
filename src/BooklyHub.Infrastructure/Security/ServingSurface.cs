using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace BooklyHub.Infrastructure.Security;

/// <summary>
/// How this host may be reached, as the operator declared it.
///
/// Both readings happen before the container is built, because what they produce is a service registration:
/// <c>UseHttpsRedirection()</c> takes no options argument on this runtime (measured: CS1501), so its port has to
/// be in <see cref="Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions"/> before <c>Build()</c>, and the
/// host's own host filter reads <see cref="Microsoft.AspNetCore.HostFiltering.HostFilteringOptions"/> the same
/// way. By the time the pipeline is written the container is frozen.
///
/// Both are fail-closed in the same direction: nothing declared leaves the deployment answering plain HTTP on
/// every <c>Host</c> value, which is the state it was in before either key was read, and is what an operator is
/// assumed to mean until they say otherwise. What this class adds is that a declaration which is <em>present and
/// unusable</em> stops the host instead of being dropped — an operator who wrote a redirect port or a host list
/// and got silence is running the deployment they configured away, with nothing saying so.
/// </summary>
public sealed record ServingSurface(int? HttpsPort, IReadOnlyList<string> AllowedHosts)
{
    public const string HttpsPortKey = "HttpsRedirection:Port";
    public const string AspNetCoreHttpsPortKey = "ASPNETCORE_HTTPS_PORT";
    public const string AllowedHostsKey = "AllowedHosts";

    /// <summary>The one host value that means "every host", and therefore no filter.</summary>
    public const string AnyHost = "*";

    public static ServingSurface FromConfiguration(IConfiguration configuration) =>
        new(ReadHttpsPort(configuration), ReadAllowedHosts(configuration));

    /// <summary>
    /// The port a plain request is sent to, or null for "do not redirect anything".
    ///
    /// A redirect needs a target this process can name. Left unset, <c>UseHttpsRedirection()</c> works the port
    /// out per request from the server's own bindings, finds none in a container that binds
    /// <c>http://+:8080</c> and holds no certificate, and writes "Failed to determine the https port for
    /// redirect" for every plain request it then serves anyway (measured). So the middleware is only worth
    /// installing when the operator names the port, which is also the only port that is right behind a
    /// TLS-terminating proxy.
    /// </summary>
    private static int? ReadHttpsPort(IConfiguration configuration)
    {
        // The dedicated key wins; ASPNETCORE_HTTPS_PORT is the spelling the host's own HTTPS layering uses, so an
        // operator who set only that one is not asking for a second, different behaviour.
        var declared = configuration[HttpsPortKey] ?? configuration[AspNetCoreHttpsPortKey];

        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        // NumberStyles.None: a value read out of an environment variable has to be the digits a port is made of.
        // Allowing signs, separators or hex would accept "0x1A" and quietly redirect somewhere else.
        if (!int.TryParse(declared.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Configuration key '{HttpsPortKey}' (or '{AspNetCoreHttpsPortKey}') contains '{declared}', which is " +
                "not a TCP port between 1 and 65535. A redirect that was asked for and silently not installed leaves " +
                "a host serving plain traffic to an operator who believes it redirects.");
        }

        return port;
    }

    /// <summary>
    /// The <c>Host</c> values this site answers on, empty for "answer on any".
    ///
    /// Host filtering is already installed by the host on this runtime, and the <c>AllowedHosts</c> key is
    /// already bound into it: measured on a build of this repository with no host-filtering line at all,
    /// <c>AllowedHosts=a.test</c> answered 200 for <c>a.test</c> and 400 for <c>evil.test</c>. So what this
    /// reading adds is not the defence. It is the two spellings that silently destroy it:
    ///
    /// - A comma. Measured on that same build, <c>AllowedHosts="a.test, b.test"</c> answered <b>400 to every
    ///   host, including the two it named</b> — the host splits on ';' only, so the whole string became one
    ///   unmatchable pattern. A comma is how an operator writing a list in a shell naturally spells it, and the
    ///   failure is a site that refuses all traffic with no startup error and nothing in the log.
    /// - An entry the matcher cannot match at all: a space inside it, or <c>*a.test</c>. Such an entry is never
    ///   an error at runtime — it simply fails to match, and what it fails to take with it depends on what else
    ///   is on the list. Alone it is the same total lockout (measured: <c>"a test"</c> answered 400 to every
    ///   Host; <c>"*a.test"</c> answered 400 to <c>xa.test</c>, the name it was meant to admit). Beside a name
    ///   that does match, it silently drops that entry's promise instead (measured: <c>"a.test; *"</c> answered
    ///   200 for <c>a.test</c> and 400 for <c>evil.test</c> — a space kept the wildcard from ever firing, so the
    ///   site restricts its hosts while its operator has declared it admits all of them).
    ///
    /// <c>*.a.test</c> is the suffix form the matcher supports and <c>*</c> alone is the "every host"
    /// declaration, so those pass. Handing the split list to the options is what fixes the comma; the rest is
    /// refused at startup, in the spirit of <see cref="ForwardingSettings"/> — a host-header rule that
    /// quietly means something other than what was written is the one failure mode an operator cannot see.
    /// </summary>
    private static IReadOnlyList<string> ReadAllowedHosts(IConfiguration configuration)
    {
        // The one key the host itself binds, as a delimited string. Measured: the indexed form an environment
        // provider builds from AllowedHosts__0 is not what the host reads, and honouring it here as well would
        // have made this code restrict hosts the deployment keeps answering. The list spelling is the one that
        // works, and docs/DEPLOYMENT.md says so.
        var declared = configuration[AllowedHostsKey];

        if (string.IsNullOrWhiteSpace(declared))
        {
            return [];
        }

        // Entries that are only delimiters are a declaration of nothing, not a malformed one — clearing the
        // variable is the safe action and must not be punished by a host that will not start.
        var hosts = declared.Split([ ';', ',' ], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (hosts.Contains(AnyHost))
        {
            if (hosts.Length > 1)
            {
                throw new InvalidOperationException(
                    $"'{AllowedHostsKey}' mixes '{AnyHost}' with named hosts ({string.Join(", ", hosts)}). " +
                    "The two declarations contradict each other and the host settles it by accident: measured, " +
                    "the named hosts answer and the wildcard never fires, so the site restricts its hosts while " +
                    "its operator has declared it admits all of them. Say which was meant.");
            }

            return [];
        }

        foreach (var host in hosts)
        {
            if (host.Any(char.IsWhiteSpace))
            {
                throw Unmatchable(host, "contains whitespace");
            }

            var wildcard = host.IndexOf('*');
            if (wildcard >= 0 && !(wildcard == 0 && host.Length > 2 && host[1] == '.'))
            {
                throw Unmatchable(host, "has a '*' that is neither a whole entry nor a leading \"*.\" prefix");
            }
        }

        return hosts;
    }

    private static InvalidOperationException Unmatchable(string host, string reason) => new(
        $"'{AllowedHostsKey}' contains '{host}', which {reason}. The host matcher compares the Host header " +
        "character by character and treats an entry it cannot match as a rule that simply never fires, so a " +
        "malformed list is not a startup error but a silently wrong site: measured, a list of nothing but " +
        "unmatchable entries answers 400 to every Host including the names spelled on it, and one sitting " +
        "beside a working name cancels exactly what it was meant to admit.");
}
