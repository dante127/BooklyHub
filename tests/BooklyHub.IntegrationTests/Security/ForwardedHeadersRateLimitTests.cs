using System.Net;
using System.Net.Http.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// The address this file's hosts put on every connection: the stand-in proxy. The in-memory host hands a
/// connection no address at all, which is not the shape of a deployment behind a load balancer, so the peer is
/// declared here and the addressless case is pinned as its own fact rather than relied on.
/// </summary>
internal static class ProxyTopology
{
    internal const string ProxyAddress = "10.0.0.9";

    /// <summary>TEST-NET-1. A proxy this file's hosts never actually connect from.</summary>
    internal const string OtherAddress = "192.0.2.1";
}

/// <summary>
/// Puts a stated address on the connection before the host's own pipeline runs, which is where a real listener
/// would have put it. Registered as a startup filter because the trust decision this file measures is made at
/// the first middleware, so the peer has to be in place before it.
/// </summary>
internal sealed class PeerAddressFilter : IStartupFilter
{
    private readonly IPAddress _peer;

    public PeerAddressFilter(IPAddress peer) => _peer = peer;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, inner) =>
        {
            context.Connection.RemoteIpAddress = _peer;
            return inner(context);
        });

        next(app);
    };
}

/// <summary>
/// A host that describes its own topology: which peer addresses its forwarded headers may be believed from, and
/// which address its connections really arrive from. Both halves are needed to test the claim, because the claim
/// is about the difference between them.
/// </summary>
public class ProxyTopologyFactory : BooklyHubWebApplicationFactory
{
    private readonly string? _peerAddress;
    private readonly string? _declaredProxies;
    private readonly string? _declaredNetworks;

    protected ProxyTopologyFactory(string? peerAddress, string? declaredProxies, string? declaredNetworks = null)
    {
        _peerAddress = peerAddress;
        _declaredProxies = declaredProxies;
        _declaredNetworks = declaredNetworks;
    }

    protected sealed override void ConfigureTestServices(IServiceCollection services)
    {
        if (_peerAddress is not null)
        {
            services.AddSingleton<IStartupFilter>(new PeerAddressFilter(IPAddress.Parse(_peerAddress)));
        }
    }

    protected sealed override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        if (_declaredProxies is not null)
        {
            configuration[BooklyHub.Infrastructure.Security.ForwardingSettings.KnownProxiesKey] = _declaredProxies;
        }

        if (_declaredNetworks is not null)
        {
            configuration[BooklyHub.Infrastructure.Security.ForwardingSettings.KnownNetworksKey] = _declaredNetworks;
        }
    }
}

internal static class SignInBurst
{
    /// <summary>The documented auth tier, spelled as a number so a walk that stops short of it is visibly short.</summary>
    public const int TierPermits = 10;

    /// <summary>
    /// An address nobody can sign in as, so the burst is paced only by the limiter and not by
    /// <c>SEC-04(b)</c>'s per-account failure count.
    /// </summary>
    private const string GuessedEmail = "guesser@nowhere.test";

    public static HttpClient Forwarded(BooklyHubWebApplicationFactory factory, string forwardedFor)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    public static async Task<HttpStatusCode> AttemptAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/v1/auth/login", new AuthController.LoginRequest(GuessedEmail, "hunter2")))
        .StatusCode;

    public static async Task AttemptsAsync(HttpClient client, int count, HttpStatusCode expected)
    {
        for (var i = 0; i < count; i++)
        {
            (await AttemptAsync(client)).Should().Be(expected,
                $"attempt {i + 1} of a walk that has to spend exactly {count} permits, " +
                "so a refusal partway means the bucket is not the one this fact is counting");
        }
    }
}

/// <summary>
/// The deployment the finding is about: a proxy in front, and the operator named it. These facts read the
/// limiter's own refusals, because the address a bucket is keyed on has no other observable face.
/// </summary>
public sealed class TrustedProxyRateLimitTests : IClassFixture<TrustedProxyRateLimitTests.DeclaredProxyFactory>
{
    public sealed class DeclaredProxyFactory : ProxyTopologyFactory
    {
        // Both declaration shapes at once: a proxy address that is not this host's peer, and the /24 the peer
        // does live in. The header is believed because of the range, which is the end-to-end proof that
        // Forwarding:KnownNetworks reaches the trust list rather than only parsing.
        public DeclaredProxyFactory() : base(ProxyTopology.ProxyAddress, ProxyTopology.OtherAddress, "10.0.0.0/24")
        {
        }
    }

    private readonly DeclaredProxyFactory _factory;

    public TrustedProxyRateLimitTests(DeclaredProxyFactory factory) => _factory = factory;

    [Fact]
    public async Task TwoCallersBehindTheDeclaredProxy_MustNotShareASignInBudget()
    {
        var burner = SignInBurst.Forwarded(_factory, "203.0.113.11");
        await SignInBurst.AttemptsAsync(burner, SignInBurst.TierPermits, HttpStatusCode.Unauthorized);

        (await SignInBurst.AttemptAsync(burner)).Should().Be(HttpStatusCode.TooManyRequests,
            "the eleventh attempt from one caller is the pattern the tier exists to stop");

        // The caller this finding is about: somebody else's burst must not be their lockout. With the header
        // unread this request carried the proxy's own address, the same one the burst above spent, and it was
        // refused on arrival.
        var stranger = SignInBurst.Forwarded(_factory, "203.0.113.12");

        (await SignInBurst.AttemptAsync(stranger)).Should().Be(HttpStatusCode.Unauthorized,
            "a caller who has made no attempt yet cannot be over a budget they never spent — this is the " +
            "refusal one anonymous burst used to be able to hand out to every other visitor of the deployment");
    }

    /// <summary>
    /// The header is a chain, and a caller writing through a believed peer can put anything in front of the
    /// address the proxy added. The chain is read from the right, so a rotating prefix rotates nothing: the
    /// identity is the address the believed peer wrote last. <see cref="AChainThatEndsWithTheDeclaredProxy_MustNotLetTheCallerReachPastIt"/>
    /// is the same claim from the other side, with the peer's own address as the prefix.
    /// </summary>
    [Fact]
    public async Task APrefixedChain_MustNotLetTheCallerChooseWhichAddressItIsBucketedUnder()
    {
        const string realClient = "203.0.113.13";
        const string freshClient = "203.0.113.14";

        for (var i = 1; i <= SignInBurst.TierPermits; i++)
        {
            // A fresh leftmost entry each time: were the leftmost the one reported, these would be ten
            // different buckets and no refusal would ever arrive.
            var client = SignInBurst.Forwarded(_factory, $"198.51.100.{i}, {realClient}");

            (await SignInBurst.AttemptAsync(client)).Should().Be(HttpStatusCode.Unauthorized,
                $"attempt {i} of the walk that has to exhaust one bucket rather than ten");
        }

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, realClient)))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "the ten prefixed attempts spent this caller's tier, and the address it cannot rotate has " +
                "nothing left");

        // Without this line the fact would pass on a host that read no header at all: every request above shares
        // one bucket there, so the refusal arrives on time for the wrong reason. A second caller proves the
        // chain was parsed and that only its last entry moved anything.
        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, $"198.51.100.99, {freshClient}")))
            .Should().Be(HttpStatusCode.Unauthorized,
                "this caller's own bucket is untouched by the burst above, which is the proof that the " +
                "leftmost address the caller wrote is not the identity the limiter used");
    }

    /// <summary>
    /// A caller can also try naming the believed peer itself: <c>X-Forwarded-For: whoever, 10.0.0.9</c> ends the
    /// chain on an address the operator declared, and the reading "walk left until an entry is not a known proxy"
    /// would put the caller's own first address in front of it and open a fresh bucket per request. Measured on
    /// this runtime that is not what happens — the entry reported is the rightmost one — and it is measured the
    /// same way with <c>ForwardLimit</c> removed, which is why this fact is credited to the peer check rather than
    /// to the limit, and why the limit is documented as a bound on the chain depth rather than as a defence.
    /// </summary>
    [Fact]
    public async Task AChainThatEndsWithTheDeclaredProxy_MustNotLetTheCallerReachPastIt()
    {
        // A burst whose every request names the believed peer as the address it came from, in front of a fresh
        // address of the caller's own choosing.
        for (var i = 1; i <= SignInBurst.TierPermits; i++)
        {
            (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, $"198.51.100.{i}, {ProxyTopology.ProxyAddress}")))
                .Should().Be(HttpStatusCode.Unauthorized,
                    $"attempt {i} of the walk that has to spend one bucket, the peer's own");
        }

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, $"198.51.100.99, {ProxyTopology.ProxyAddress}")))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "an address the operator named is not a door a caller can walk past by writing it down: this " +
                "burst is one bucket, the proxy's, and by the eleventh request it is out of permits");
    }
}

/// <summary>
/// The control that keeps the class above from passing on a middleware that believes anybody: the feature is on
/// and a proxy is named, and this host's connections do not arrive from it.
/// </summary>
public sealed class UndeclaredProxyRateLimitTests : IClassFixture<UndeclaredProxyRateLimitTests.WrongProxyFactory>
{
    public sealed class WrongProxyFactory : ProxyTopologyFactory
    {
        public WrongProxyFactory() : base(ProxyTopology.ProxyAddress, ProxyTopology.OtherAddress)
        {
        }
    }

    private readonly WrongProxyFactory _factory;

    public UndeclaredProxyRateLimitTests(WrongProxyFactory factory) => _factory = factory;

    [Fact]
    public async Task AForwardedAddressFromAnUndeclaredPeer_MustBuyTheBurstNothing()
    {
        await SignInBurst.AttemptsAsync(SignInBurst.Forwarded(_factory, "203.0.113.21"),
            SignInBurst.TierPermits, HttpStatusCode.Unauthorized);

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, "203.0.113.22")))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "two callers writing their own header from a peer nobody named are one bucket. Without this, " +
                "anyone could buy an unlimited sign-in budget by naming an address in the header");
    }
}

/// <summary>
/// The runtime's own default, measured: <c>ForwardedHeadersOptions</c> is constructed already trusting loopback,
/// so naming a remote proxy would leave every process on the same machine — a sidecar, another container on the
/// host network — able to write its own client identity. The host clears what no operator declared, and this is
/// the fact that says that clearing is load-bearing rather than tidying.
/// </summary>
public sealed class LoopbackProxyRateLimitTests : IClassFixture<LoopbackProxyRateLimitTests.LoopbackPeerFactory>
{
    public sealed class LoopbackPeerFactory : ProxyTopologyFactory
    {
        public LoopbackPeerFactory() : base("127.0.0.1", ProxyTopology.OtherAddress)
        {
        }
    }

    private readonly LoopbackPeerFactory _factory;

    public LoopbackProxyRateLimitTests(LoopbackPeerFactory factory) => _factory = factory;

    [Fact]
    public async Task AConnectionFromLoopback_MustNotBeBelievedJustBecauseTheFrameworkSaysSo()
    {
        await SignInBurst.AttemptsAsync(SignInBurst.Forwarded(_factory, "203.0.113.31"),
            SignInBurst.TierPermits, HttpStatusCode.Unauthorized);

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, "203.0.113.32")))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "the declared list is the whole list: with the inherited loopback entry left in place, these " +
                "two addresses were two buckets and a local process could rotate past the tier at will");
    }
}

/// <summary>
/// A host that has not described its topology declares no proxy, and must behave as it did before this setting
/// existed: no header read, and the same shared bucket for everybody behind whatever sits in front. That shared
/// bucket is a stated consequence of not configuring, not a silent default a caller can mistake for safety.
/// </summary>
public sealed class NoForwardingConfigRateLimitTests : IClassFixture<NoForwardingConfigRateLimitTests.PlainPeerFactory>
{
    public sealed class PlainPeerFactory : ProxyTopologyFactory
    {
        public PlainPeerFactory() : base(ProxyTopology.ProxyAddress, declaredProxies: null)
        {
        }
    }

    private readonly PlainPeerFactory _factory;

    public NoForwardingConfigRateLimitTests(PlainPeerFactory factory) => _factory = factory;

    [Fact]
    public async Task AHostThatDeclinesToNameItsProxy_MustIgnoreTheHeaderEntirely()
    {
        await SignInBurst.AttemptsAsync(SignInBurst.Forwarded(_factory, "203.0.113.41"),
            SignInBurst.TierPermits, HttpStatusCode.Unauthorized);

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, "203.0.113.42")))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "with nothing declared the middleware is not registered at all, so an unconfigured host keeps " +
                "the behaviour it had before this setting existed instead of guessing at a trust nobody asked for");
    }
}

/// <summary>
/// The remaining way a believed header could still be a lie: a connection that carries no address. Kestrel gives
/// every socket one, a unix-socket listener gives none, and the framework's answer to "which peer is this?" with
/// no peer at all is to read the header anyway.
/// </summary>
public sealed class AddresslessConnectionRateLimitTests : IClassFixture<AddresslessConnectionRateLimitTests.AddresslessFactory>
{
    public sealed class AddresslessFactory : ProxyTopologyFactory
    {
        // No peer filter, so the connections keep the in-memory host's own addressless shape, and the
        // declaration is one this host never connects from.
        public AddresslessFactory() : base(peerAddress: null, ProxyTopology.OtherAddress)
        {
        }
    }

    private readonly AddresslessFactory _factory;

    public AddresslessConnectionRateLimitTests(AddresslessFactory factory) => _factory = factory;

    [Fact]
    public async Task AConnectionWithNoAddress_MustNotBeTreatedAsAPeerThatWasNamed()
    {
        await SignInBurst.AttemptsAsync(SignInBurst.Forwarded(_factory, "203.0.113.51"),
            SignInBurst.TierPermits, HttpStatusCode.Unauthorized);

        (await SignInBurst.AttemptAsync(SignInBurst.Forwarded(_factory, "203.0.113.52")))
            .Should().Be(HttpStatusCode.TooManyRequests,
                "there is no peer to compare against, which is a refusal rather than a blank cheque: measured " +
                "before the guard, these two addresses were two buckets and any local process over a " +
                "unix socket could name whichever client it liked");
    }
}
