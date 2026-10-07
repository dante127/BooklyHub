using BooklyHub.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BooklyHub.UnitTests.Infrastructure;

/// <summary>
/// The declaration half of the serving surface: what an operator has to write for the host to redirect, and what
/// it refuses to start on. The host-filtering half is covered here for the reading only — the measured difference
/// between this and the host's own binding of the same key (a comma list, which the host turns into a site that
/// answers nobody) is a fact about a running pipeline, and the live matrix behind it is recorded in
/// docs/DEPLOYMENT.md.
/// </summary>
public class ServingSurfaceTests
{
    private static ServingSurface Read(Dictionary<string, string?> values) =>
        ServingSurface.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void AHostWithNoDeclaration_InstallsNeitherRedirectNorFilter()
    {
        var surface = Read([]);

        surface.HttpsPort.Should().BeNull(
            "with no port named there is nothing to redirect to, and the middleware spends every plain request " +
            "failing to work one out");
        surface.AllowedHosts.Should().BeEmpty(
            "an empty list is how this code says \"do not narrow the Host values\", which is the state the " +
            "deployment was in before the key was read at all");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankHostList_MustReadAsNoNarrowing_NotAsABrokenOne(string value)
    {
        var surface = Read(new Dictionary<string, string?> { [ServingSurface.AllowedHostsKey] = value });

        surface.AllowedHosts.Should().BeEmpty(
            "clearing the variable is the safe action, and a host that crashed on an empty string would punish " +
            "exactly the operator who did the right thing");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("  *  ")]
    public void TheWildcard_IsTheOneDeclarationThatMeansNoNarrowing(string value)
    {
        var surface = Read(new Dictionary<string, string?> { [ServingSurface.AllowedHostsKey] = value });

        surface.AllowedHosts.Should().BeEmpty(
            "appsettings.json ships AllowedHosts as \"*\", which is the repository saying \"every host\", not " +
            "the operator naming one");
    }

    [Theory]
    [InlineData("bookly.example; api.bookly.example")]
    [InlineData("bookly.example, api.bookly.example")]
    [InlineData(" bookly.example ;api.bookly.example ")]
    [InlineData("bookly.example;;api.bookly.example")]
    public void SeveralNamesInOneVariable_MustAllBeAnswered(string value)
    {
        var surface = Read(new Dictionary<string, string?> { [ServingSurface.AllowedHostsKey] = value });

        surface.AllowedHosts.Should().BeEquivalentTo(["bookly.example", "api.bookly.example"],
            "the host splits this key on ';' alone, so a comma — the way a list is naturally written in a shell " +
            "— leaves one unmatchable entry and the site answers 400 to every Host, including the two it names " +
            "(measured). Reading both delimiters is the whole of the difference");
    }

    [Fact]
    public void ASuffixWildcard_MustSurvive_TheWayTheMatcherSupportsIt()
    {
        var surface = Read(new Dictionary<string, string?>
        {
            [ServingSurface.AllowedHostsKey] = "*.bookly.example"
        });

        surface.AllowedHosts.Should().BeEquivalentTo(["*.bookly.example"],
            "\"*.example\" is the one wildcard form the host matcher reads besides a bare \"*\", and refusing it " +
            "would rule out the declaration an operator of a multi-subdomain site has to write");
    }

    [Theory]
    [InlineData("*a.test")]
    [InlineData("a*b.test")]
    [InlineData("bookly.example*")]
    public void AWildcardInTheWrongPlace_MustRefuseTheHost(string value)
    {
        var act = () => Read(new Dictionary<string, string?> { [ServingSurface.AllowedHostsKey] = value });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{value}'*",
            "measured: an entry the matcher cannot match is not skipped, it makes the whole list refuse every " +
            "request — so this spelling is a locked site, and the only safe moment to say so is startup");
    }

    [Theory]
    [InlineData("a test.example")]
    [InlineData("bookly.example api.bookly.example")]
    public void AHostEntryWithWhitespaceInsideIt_MustRefuseTheHost(string value)
    {
        var act = () => Read(new Dictionary<string, string?> { [ServingSurface.AllowedHostsKey] = value });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{value}'*",
            "a space inside an entry survives trimming and produces exactly the lockout the misplaced wildcard " +
            "does; the delimiter cases above are safe precisely because the split removes their whitespace");
    }

    [Fact]
    public void AWildcardBesideAName_MustRefuseTheHostRatherThanPickOne()
    {
        var act = () => Read(new Dictionary<string, string?>
        {
            [ServingSurface.AllowedHostsKey] = "bookly.example; *"
        });

        var failure = act.Should().Throw<InvalidOperationException>(
            "the wildcard admits every host, so the name beside it does nothing: an operator who wrote both and " +
            "got a narrowed list would believe their site restricts its host names").Which;

        failure.Message.Should().Contain("bookly.example").And.Contain(ServingSurface.AllowedHostsKey,
            "the reason to refuse at startup is that somebody can fix it, which needs the list and the key it " +
            "came from in the message");
    }

    [Fact]
    public void TheDedicatedPortKey_WinsOverTheAspNetCoreSpelling()
    {
        var surface = Read(new Dictionary<string, string?>
        {
            [ServingSurface.HttpsPortKey] = "8443",
            [ServingSurface.AspNetCoreHttpsPortKey] = "443"
        });

        surface.HttpsPort.Should().Be(8443,
            "the two keys say the same thing in two vocabularies, and the one named for this feature is the one " +
            "that has to win when an operator wrote both");
    }

    [Fact]
    public void TheAspNetCoreSpelling_AloneIsADeclarationToo()
    {
        var surface = Read(new Dictionary<string, string?>
        {
            [ServingSurface.AspNetCoreHttpsPortKey] = "443"
        });

        surface.HttpsPort.Should().Be(443,
            "ASPNETCORE_HTTPS_PORT is what a TLS-terminating proxy deployment already sets for the host's own " +
            "HTTPS layering, and a redirect that ignores it redirects to the wrong port");
    }

    [Theory]
    [InlineData("443")]
    [InlineData(" 8443 ")]
    [InlineData("8081")]
    [InlineData("65535")]
    public void APortTheOperatorNamed_MustBeThePortTheRedirectUses(string value)
    {
        var surface = Read(new Dictionary<string, string?> { [ServingSurface.HttpsPortKey] = value });

        surface.HttpsPort.Should().Be(int.Parse(value),
            "this value is the whole of what the redirect middleware is configured with, so a reading that " +
            "changed it would send traffic somewhere else");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("wide")]
    [InlineData("0x1A")]
    [InlineData("+443")]
    [InlineData("4,43")]
    public void APortThatIsNotAPort_MustStopTheHostFromStarting(string value)
    {
        var act = () => Read(new Dictionary<string, string?> { [ServingSurface.HttpsPortKey] = value });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{value}'*",
            "a redirect that was asked for and silently not installed leaves plain traffic served to an " +
            "operator who believes it redirects; and a value that parsed loosely — 0x1A, 4,43 — would redirect " +
            "to a port nobody named");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankRedirectPort_MustDisableTheRedirect_NotRefuseTheHost(string value)
    {
        var surface = Read(new Dictionary<string, string?> { [ServingSurface.HttpsPortKey] = value });

        surface.HttpsPort.Should().BeNull(
            "${HTTPS_REDIRECT_PORT:-} in a compose file produces exactly this empty string when the operator " +
            "left it unset, and the state that describes is the one where this host serves plain HTTP behind a " +
            "proxy that redirects on its own");
    }

    [Fact]
    public void DeclarationsThatDoNotOverlap_MustNotInterfere()
    {
        var surface = Read(new Dictionary<string, string?>
        {
            [ServingSurface.AllowedHostsKey] = "bookly.example",
            [ServingSurface.AspNetCoreHttpsPortKey] = "443"
        });

        surface.AllowedHosts.Should().ContainSingle("the host list is read from its own key");
        surface.HttpsPort.Should().Be(443, "and the redirect port from its own, so one setting cannot shadow the other");
    }
}
