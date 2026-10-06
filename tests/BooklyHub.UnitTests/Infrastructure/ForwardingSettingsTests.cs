using BooklyHub.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BooklyHub.UnitTests.Infrastructure;

/// <summary>
/// The declaration half of the forwarded-header fix: what an operator has to write for the host to believe a
/// proxy, and what it refuses to start on. The live half — what a believed header does to a bucket — is in
/// ForwardedHeadersRateLimitTests.
/// </summary>
public class ForwardingSettingsTests
{
    private static ForwardingSettings Read(Dictionary<string, string?> values) =>
        ForwardingSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void AHostWithNoDeclaration_HasNothingToTrust()
    {
        var settings = Read([]);

        settings.IsConfigured.Should().BeFalse(
            "an empty list is the fail-closed state: a host that has not described its topology reads no " +
            "forwarded header at all rather than guessing which peer its traffic comes from");
        settings.KnownProxies.Should().BeEmpty();
        settings.KnownNetworks.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSetting_MustReadAsNoDeclaration_NotAsABrokenOne(string value)
    {
        var settings = Read(new Dictionary<string, string?>
        {
            [ForwardingSettings.KnownProxiesKey] = value
        });

        settings.IsConfigured.Should().BeFalse(
            "an operator who clears the variable clears the trust, and a host that crashed on an empty string " +
            "would punish exactly the safe action");
    }

    [Fact]
    public void OneVariableHoldingACommaSeparatedList_MustBeSeveralProxies()
    {
        var settings = Read(new Dictionary<string, string?>
        {
            [ForwardingSettings.KnownProxiesKey] = "10.0.0.5, 10.0.0.6"
        });

        settings.KnownProxies.Should().BeEquivalentTo([System.Net.IPAddress.Parse("10.0.0.5"), System.Net.IPAddress.Parse("10.0.0.6")],
            "a docker-compose environment variable is written on one line, and an operator who listed two " +
            "proxies must not get a host that trusts only the first");
    }

    [Fact]
    public void TheIndexedFormTheConfigurationSystemProduces_MustBeReadAsWell()
    {
        var settings = Read(new Dictionary<string, string?>
        {
            [ForwardingSettings.KnownProxiesKey + ":0"] = "10.0.0.5",
            [ForwardingSettings.KnownProxiesKey + ":1"] = "10.0.0.6"
        });

        settings.KnownProxies.Should().HaveCount(2,
            "Forwarding__KnownProxies__0 is the shape the environment provider actually builds, and the " +
            "comma-separated form above is the shape an operator writes");
    }

    [Fact]
    public void ACidrBlock_MustBeANetworkAndALoneDeclaration()
    {
        var settings = Read(new Dictionary<string, string?>
        {
            [ForwardingSettings.KnownNetworksKey] = "10.2.0.0/16"
        });

        settings.IsConfigured.Should().BeTrue("a proxy pool that shares a range is a topology somebody did name");
        settings.KnownNetworks.Should().ContainSingle("the range has to reach the trust list, not only parse");
        settings.KnownProxies.Should().BeEmpty("the two settings are separate lists and one does not imply the other");
    }

    [Theory]
    [InlineData(ForwardingSettings.KnownProxiesKey, "10.0.0.256")]
    [InlineData(ForwardingSettings.KnownProxiesKey, "10.0.0.5/8")]
    [InlineData(ForwardingSettings.KnownNetworksKey, "10.0.0.0")]
    [InlineData(ForwardingSettings.KnownNetworksKey, "wide")]
    public void AMalformedEntry_MustStopTheHostFromStarting(string key, string entry)
    {
        var act = () => Read(new Dictionary<string, string?> { [key] = entry });

        var failure = act.Should().Throw<InvalidOperationException>(
            "a trust list that silently lost an entry leaves the deployment in the shared-bucket behaviour it " +
            "was configured to leave, and nothing would say so").Which;

        failure.Message.Should().Contain(entry).And.Contain(key,
            "the reason to refuse at startup is that an operator can fix it, which needs the entry and the key " +
            "it came from in the message");
    }

    [Fact]
    public void AHostNameInPlaceOfAnAddress_MustBeRefusedForTheReason_ItIsRefused()
    {
        var act = () => Read(new Dictionary<string, string?>
        {
            [ForwardingSettings.KnownProxiesKey] = "edge.example.test"
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Host names are not accepted*",
            "resolving a name at startup would trust whatever DNS says today, while the peer list is about the " +
            "address a connection actually arrived from");
    }
}
