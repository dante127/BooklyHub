using System.Net;
using System.Text;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// `SEC-11` claimed a `search` term reaches SQL as <c>LIKE '%…%'</c> with <c>%</c> and <c>_</c> intact, so a caller
/// typing a wildcard could turn one customer's search box into a match-anything query. Measured on this host, that
/// is not what happens: EF Core translates the predicate to <c>LIKE @search_contains ESCAPE N'\'</c> and puts the
/// escape character in front of <c>%</c>, <c>_</c>, <c>[</c> and the escape character itself before the term ever
/// leaves the process. These facts are written so the claim stays measured rather than inherited: they are the
/// regression test for a behaviour the application does not implement but depends on, and the control that builds
/// the pattern by hand (<see cref="LikeWildcardSearchTests"/> notes in its remarks) is what says so.
/// </summary>
public class LikeWildcardSearchTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public LikeWildcardSearchTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(_tenantA, "Wildcard Clinic", $"wildcard-{_tenantA:N}", "UTC"));
        db.Tenants.Add(new Tenant(_tenantB, "Other Clinic", $"other-{_tenantB:N}", "UTC"));

        void Add(Guid tenant, string first, string last, string email) => db.Customers.Add(new Customer
        {
            TenantId = tenant,
            FirstName = first,
            LastName = last,
            Email = email
        });

        // The names are chosen so each wildcard shape has one answer: a row that contains the character itself,
        // and rows that only a wild-card-reading engine would bring back.
        Add(_tenantA, "50% Off", "Promo", "promo@probe.test");
        Add(_tenantA, "Ana", "Kit", "ana.kit@probe.test");
        Add(_tenantA, "Bna", "Bracket", "bracket@probe.test");
        Add(_tenantA, "A_b", "Under", "under@probe.test");
        Add(_tenantA, "Back\\slash", "Escaper", "escaper@probe.test");
        Add(_tenantA, "Ali", "Hassan", "ali@probe.test");
        Add(_tenantB, "Anyone", "Elsewhere", "anyone@elsewhere.test");

        db.SaveChanges();
    }

    private sealed record Answer(int Status, int Total, string[] FirstNames, string[] Sql)
    {
        public string Found => string.Join(", ", FirstNames);
    }

    /// <summary>One search through the route, plus the statements the server actually sent for it.</summary>
    private async Task<Answer> SearchAsync(string term, Guid? tenant = null)
    {
        using var client = _factory.CreateClientForTenant(tenant ?? _tenantA, Roles.TenantOwner);
        _factory.QueryInterceptor.Reset();

        var response = await client.GetAsync($"/api/v1/customers?search={Uri.EscapeDataString(term)}&pageSize=100");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var names = response.StatusCode == HttpStatusCode.OK
            ? body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("firstName").GetString() ?? "?").ToArray()
            : [];

        return new Answer(
            (int)response.StatusCode,
            body.TryGetProperty("total", out var total) && total.TryGetInt32(out var n) ? n : -1,
            names,
            _factory.QueryInterceptor.ExecutedCommands.Where(t => t.Contains("[Customers]", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task APercentInTheSearchTerm_MustMatchAPercentAndNotTheWholeBook()
    {
        var answer = await SearchAsync("%");

        answer.Status.Should().Be(200);
        answer.Total.Should().Be(1, "one of these names has a percent sign in it; a wildcard would match all six");
        answer.Found.Should().Be("50% Off");
    }

    [Fact]
    public async Task AnUnderscoreInTheSearchTerm_MustNotStandInForOneCharacter()
    {
        // "a_a" is three characters with a wildcard in the middle if the engine reads it that way: it would find
        // Ana. Read literally it is a four-character substring and no row holds it.
        var answer = await SearchAsync("a_a");

        answer.Total.Should().Be(0, "the term asked for the characters a _ a, and no name contains them");
    }

    [Fact]
    public async Task ABracketInTheSearchTerm_MustNotBecomeACharacterClass()
    {
        // SQL Server reads [AB]na as "Ana or Bna", which are two of the seeded rows. It is the shape an escape of
        // only % and _ would miss, so it is measured rather than assumed from the two above.
        var answer = await SearchAsync("[AB]na");

        answer.Total.Should().Be(0);
    }

    [Fact]
    public async Task ASecondWildcardCharacter_MustNotMakeTheEscapeRuleFaultOnItsOwnCharacter()
    {
        // The escape character is the one a partial escape rule leaves behind: an unpaired \ in a LIKE pattern is
        // an error ("undefined escape sequence"), which would be a 500 for a caller typing a backslash.
        var literal = await SearchAsync("\\");
        literal.Status.Should().Be(200);
        literal.Found.Should().Be("Back\\slash", "the term is the character itself, and one name contains it");

        var mixed = await SearchAsync("\\%");
        mixed.Status.Should().Be(200);
        mixed.Total.Should().Be(0, "no name contains a backslash followed by a percent sign");
    }

    [Fact]
    public async Task AQuotedInjectionInTheSearchTerm_MustBeAParameterAndNotSyntax()
    {
        var answer = await SearchAsync("' OR 1=1--");

        answer.Status.Should().Be(200);
        answer.Total.Should().Be(0, "the term is a string to look for, not a clause");
        answer.Sql.Should().NotBeEmpty("a search that reaches no statement proves nothing about how it was sent");
        answer.Sql.Should().OnlyContain(
            text => !text.Contains("OR 1=1", StringComparison.Ordinal),
            "the caller's text must never be spliced into the statement");
        answer.Sql.Should().Contain(
            text => text.Contains("LIKE @", StringComparison.Ordinal) && text.Contains("ESCAPE", StringComparison.Ordinal),
            "the escaping is the framework's own, in the statement the server sent");
    }

    [Fact]
    public async Task AMatchAnythingSearch_MustNotCrossIntoAnotherTenant()
    {
        var answer = await SearchAsync("%", _tenantB);

        answer.Total.Should().Be(0, "tenant B has one row and its name has no percent sign in it");
        answer.Found.Should().NotContain("Anyone");
    }
}

/// <summary>
/// The half the audit did not name, found by walking the term length up until the route stopped answering: from
/// 3,999 characters the caller's term becomes a <c>LIKE</c> pattern longer than SQL Server's 4,000-character
/// ceiling and <c>GET /api/v1/customers</c> answers <c>500</c> with the contact-support text. That is the same class
/// as <c>PAG-01</c> — a caller's value, no answer for it — and it is closed the same way: by a bound. The bound is
/// not a taste number: every column the term is matched against is at most 256 characters, so a longer term cannot
/// be in any row, and an empty page is the only true answer it could get anyway.
/// </summary>
public class SearchTermLengthTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;
    private readonly Guid _tenantId = Guid.NewGuid();

    /// <summary>256 characters — the longest string the widest column the predicate reads can hold.</summary>
    private readonly string _widestEmail = new string('z', 256 - "@widest.test".Length) + "@widest.test";

    public SearchTermLengthTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(_tenantId, "Long Term Clinic", $"long-{_tenantId:N}", "UTC"));
        db.Customers.Add(new Customer { TenantId = _tenantId, FirstName = "Wide", LastName = "Email", Email = _widestEmail });
        db.Customers.Add(new Customer { TenantId = _tenantId, FirstName = "Filler", LastName = "One", Email = "filler1@probe.test" });
        db.Customers.Add(new Customer { TenantId = _tenantId, FirstName = "Filler", LastName = "Two", Email = "filler2@probe.test" });

        db.SaveChanges();
    }

    private async Task<(int Status, int Total, int Items)> SearchAsync(string term)
    {
        using var client = _factory.CreateClientForTenant(_tenantId, Roles.TenantOwner);
        var response = await client.GetAsync($"/api/v1/customers?search={Uri.EscapeDataString(term)}&pageSize=100");
        var text = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK) return ((int)response.StatusCode, -1, -1);

        var body = JsonDocument.Parse(text).RootElement;
        return (
            (int)response.StatusCode,
            body.GetProperty("total").GetInt32(),
            body.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    [InlineData(12000)]
    public async Task ATermLongerThanAnyColumn_MustBeAnEmptyPageRatherThanA500(int length)
    {
        var (status, total, items) = await SearchAsync(new string('q', length));

        status.Should().Be(200, $"a search box can be pasted into; measured before the bound, {length} characters " +
                                "answered 500 because the LIKE pattern it becomes is over SQL Server's 4,000-character ceiling");
        total.Should().Be(0);
        items.Should().Be(0);
    }

    [Fact]
    public async Task ATermInsideTheBound_MustStillFindTheRowThatFillsTheWidestColumn()
    {
        // The bound is only fair if nothing real falls off it: this email is the longest string the widest matched
        // column can hold, and searching for it has to work.
        _widestEmail.Length.Should().Be(CustomersController.MaxSearchTermCharacters);

        var (status, total, items) = await SearchAsync(_widestEmail);
        status.Should().Be(200);
        total.Should().Be(1);
        items.Should().Be(1);
    }

    [Fact]
    public async Task ATermJustOverTheBound_MustNotBeTruncatedIntoSomethingThatMightMatch()
    {
        var (status, total, _) = await SearchAsync(_widestEmail + "q");

        status.Should().Be(200);
        total.Should().Be(0, "the term is 257 characters and no column it is matched against holds that many");
    }

    [Fact]
    public async Task ATermMadeOfNothing_MustStillBeTheWholeBookEvenAtFourThousandCharacters()
    {
        // The rule this pins is the binder's, not the guard's: MVC hands a string route parameter nothing-but-
        // spaces as null, so a wall of spaces is the same question as no search at all — the whole book. Measured
        // as a control: a guard that skipped the length check on a blank term could not break this, because no
        // blank term ever reaches it.
        var (status, total, items) = await SearchAsync(new string(' ', 4000));

        status.Should().Be(200);
        total.Should().Be(3);
        items.Should().Be(3);
    }

    [Fact]
    public void TheBound_MustBeSetByTheColumnsItIsComparedAgainstAndStayUnderThePatternCeiling()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var customer = db.Model.FindEntityType(typeof(Customer))!;
        var widths = new[] { nameof(Customer.FirstName), nameof(Customer.LastName), nameof(Customer.Email) }
            .Select(p => customer.FindProperty(p)!.GetMaxLength() ?? 0)
            .ToArray();

        widths.Should().AllSatisfy(w => w.Should().BeGreaterThan(0, "an unbounded column would make the term bound a guess"));
        CustomersController.MaxSearchTermCharacters.Should().Be(
            widths.Max(),
            "a term longer than the widest column the predicate reads cannot match, which is the whole argument for the bound");

        // Every character of the term can be escaped into two, plus the two wrapping wildcards.
        (2 + 2 * CustomersController.MaxSearchTermCharacters).Should().BeLessThanOrEqualTo(4000,
            "the worst-case pattern this route sends has to stay inside what SQL Server will run");
    }
}
