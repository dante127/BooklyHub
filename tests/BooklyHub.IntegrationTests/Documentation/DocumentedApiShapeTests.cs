using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using BooklyHub.IntegrationTests.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Documentation;

/// <summary>
/// DOC-01 and DOC-03 are the same defect in two places: a document that states a route or a response shape the code
/// does not have. <c>docs/API.md</c> documented <c>GET /api/v1/availability/slots</c> and
/// <c>POST /api/v1/appointments/{id}/status</c> — neither exists — and its login sample listed flat
/// <c>userId</c>/<c>fullName</c> keys the action never returns. A reader follows the document, so the cost of each
/// one is a client that codes against a URL that 404s or a field that is missing.
/// </summary>
/// <remarks>
/// These facts read the document itself, so the fixture is the thing that is supposed to drift: correcting a sample
/// in <c>API.md</c> is a change these tests react to. The route assertion here runs in one direction — documented
/// paths must exist — and its reverse, that every route the server answers on has a section, is
/// <see cref="EveryEndpointIsDocumentedTests"/> (<c>DOC-04</c>). Two directions stay two facts because they fail for
/// opposite reasons: a heading naming nothing is a client being sent to a 404, and a live route with no heading is a
/// client inventing a contract nobody wrote down.
/// </remarks>
internal static class ApiDoc
{
    private static readonly Lazy<string> FileText = new(Load);

    /// <summary>
    /// Walks up from the test assembly's directory to the repository's docs folder. If it ever stops finding the
    /// file the assertion below would pass on an empty set, so the search is reported when it fails.
    /// </summary>
    private static string Load()
    {
        var searched = new List<string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "API.md");
            searched.Add(candidate);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException(
            $"docs/API.md was not found above {AppContext.BaseDirectory}. Tried: {string.Join(" | ", searched)}");
    }

    public static IReadOnlyList<(string Verb, string Path)> DocumentedRoutes()
    {
        var matches = Regex.Matches(FileText.Value,
            @"\b(GET|POST|PUT|PATCH|DELETE)\s+(/api/v1/[^\s`<>)""\]]*)");

        return matches.Select(m => (m.Groups[1].Value, Normalize(m.Groups[2].Value.TrimEnd('.', ',', ';'))))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The paths under a <c>### `METHOD /path`</c> heading — the document's own claim that a route exists and is
    /// described. Deliberately narrower than <see cref="DocumentedRoutes"/>, which also catches the prose mentions a
    /// section makes of the verb its caller uses next; a bullet that names a route is not a reader being sent to it.
    /// </summary>
    public static IReadOnlyList<(string Verb, string Path)> DocumentedHeadings()
    {
        var matches = Regex.Matches(FileText.Value,
            @"^### `(?<verb>GET|POST|PUT|PATCH|DELETE) (?<path>/api/v1/[^\s`]*)`",
            RegexOptions.Multiline);

        return matches.Select(m => (m.Groups["verb"].Value, Normalize(m.Groups["path"].Value)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The route table as the host actually built it, in the same (verb, normalized path) shape the document is read
    /// in. Both directions of the comparison go through this, so a disagreement cannot come from two different
    /// notions of what the server exposes.
    /// </summary>
    public static IReadOnlyList<(string Verb, string Path)> ImplementedRoutes(BooklyHubWebApplicationFactory factory)
    {
        var provider = factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        return provider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(d => d.HttpMethod is not null)
            .Select(d => (d.HttpMethod!, Normalize(d.RelativePath!)))
            .Distinct()
            .ToList();
    }

    /// <summary>Route parameters are placeholders to a client, and <c>{id}</c> and <c>{id:guid}</c> are one path.</summary>
    public static string Normalize(string path) =>
        Regex.Replace(path.Split('?')[0].TrimStart('/'), @"\{[^}]*\}", "{}").TrimEnd('/');

    /// <summary>
    /// The key paths of the ```json sample under the <c>**Response</c> block of the heading that contains
    /// <paramref name="heading"/>, with array elements flattened as <c>slots[].startAtUtc</c>. Values are not
    /// compared: this is about which fields a client is told exist. The response marker matters because most
    /// sections show a request payload first, and a request sample would prove nothing about the answer.
    /// </summary>
    public static IReadOnlyList<string> SampleKeyPaths(string heading)
    {
        var lines = FileText.Value.Replace("\r\n", "\n").Split('\n');
        var start = IndexOf(lines, 0, lines.Length, l => l.StartsWith("### ") && l.Contains(heading));
        if (start < 0)
        {
            throw new InvalidOperationException($"docs/API.md has no '### ' heading containing '{heading}'");
        }

        var limit = IndexOf(lines, start + 1, lines.Length, l => l.StartsWith("### "));
        if (limit < 0) limit = lines.Length;

        var response = IndexOf(lines, start + 1, limit, l => l.StartsWith("**Response"));
        if (response < 0)
        {
            throw new InvalidOperationException($"the '{heading}' section of docs/API.md has no **Response block to read");
        }

        var fence = IndexOf(lines, response, limit, l => l.StartsWith("```json"));
        if (fence < 0)
        {
            throw new InvalidOperationException($"the **Response block under '{heading}' has no ```json sample");
        }

        var closing = IndexOf(lines, fence + 1, lines.Length, l => l.StartsWith("```"));
        if (closing < 0)
        {
            throw new InvalidOperationException($"the json sample under '{heading}' is never closed");
        }

        var body = new List<string>();
        for (var i = fence + 1; i < closing; i++) body.Add(lines[i]);

        using var document = JsonDocument.Parse(string.Join('\n', body));
        return Paths(document.RootElement, string.Empty).Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>Searches a line range without the overload games Array.FindIndex plays with a bounds predicate.</summary>
    private static int IndexOf(string[] lines, int from, int to, Func<string, bool> match)
    {
        for (var i = from; i < to; i++)
        {
            if (match(lines[i])) return i;
        }

        return -1;
    }

    private static IEnumerable<string> Paths(JsonElement element, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    yield return path;
                    foreach (var nested in Paths(property.Value, path))
                        yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Paths(item, $"{prefix}[]"))
                        yield return nested;
                }
                break;
        }
    }

    /// <summary>The same flattening over a real response body, so the two sides of the comparison are the same function.</summary>
    public static IReadOnlyList<string> ResponseKeyPaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Paths(document.RootElement, string.Empty).Distinct().OrderBy(p => p).ToList();
    }
}

public class DocumentedApiShapeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly BooklyHubWebApplicationFactory _factory;

    public DocumentedApiShapeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void EveryRouteTheDocsDocument_MustExistInTheRouteTable()
    {
        var implemented = ApiDoc.ImplementedRoutes(_factory).ToHashSet();

        var documented = ApiDoc.DocumentedRoutes();
        documented.Should().NotBeEmpty("a document with no routes would make this fact pass by saying nothing");

        // Collected, not failed on the first: one run should name every path a client would be sent to.
        var missing = documented.Where(d => !implemented.Contains((d.Verb, d.Path))).ToList();
        missing.Should().BeEmpty(
            $"docs/API.md points clients at routes the controllers do not have: {string.Join(", ", missing.Select(m => $"{m.Verb} /{m.Path}"))}");
    }

    [Fact]
    public async Task TheLoginSample_MustNameTheKeysTheActionReturns()
    {
        var email = $"doc03.login.{Guid.NewGuid():N}@example.test";
        await AuthWire.SeedUserAsync(_factory, email);

        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = AuthWire.Password });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var actual = ApiDoc.ResponseKeyPaths(body);
        actual.Should().Contain("user.roles",
            "the response really does nest the identity, so the sample has to name it nested");

        // Collected rather than compared with Equal so a drift says which keys moved in which direction.
        var sample = ApiDoc.SampleKeyPaths("POST /api/v1/auth/login");
        actual.Should().BeEquivalentTo(sample,
            "the sample is a claim about which fields a client may read, and a nested user object is not a flat fullName");
    }

    [Fact]
    public async Task TheAvailabilitySample_MustNameTheKeysTheActionReturns()
    {
        var graph = await SeedBookableDayAsync();

        _factory.Clock.Pin(Now);
        try
        {
            var response = await _factory.CreateClient().GetAsync(
                $"/api/v1/availability?tenantId={graph.TenantId}&locationId={graph.LocationId}" +
                $"&serviceId={graph.ServiceId}&staffId={graph.StaffId}&date=2026-10-05");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            ApiDoc.ResponseKeyPaths(body).Should().Contain("slots[].startAtUtc",
                "an empty page would let this fact compare two shapes it never saw a slot of");

            ApiDoc.ResponseKeyPaths(body).Should().BeEquivalentTo(ApiDoc.SampleKeyPaths("GET /api/v1/availability"),
                "the sample is the contract the public booking portal is built from");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId);

    /// <summary>A tenant that configured everything, on a Monday with a shift, so the grid answers with slots.</summary>
    private async Task<Graph> SeedBookableDayAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, "Docs Clinic", $"docs-{graph.TenantId:N}", "UTC"));
        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
            TenantId = graph.TenantId,
            Name = "Main",
            Address = "1 Main St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });
        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ava",
            LastName = "Doc",
            Email = $"ava-{graph.StaffId:N}@docs.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            ServiceId = graph.ServiceId
        });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = DayOfWeek.Monday,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.TenantSettings.Add(new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 0,
            MaxAdvanceBookingDays = 365,
            SlotIntervalMinutes = 15
        });

        await db.SaveChangesAsync();

        (await db.TenantSettings.IgnoreQueryFilters().AnyAsync(s => s.TenantId == graph.TenantId))
            .Should().BeTrue("this fixture needs the configured tenant, the opposite of the fallback facts");

        return graph;
    }
}

/// <summary>
/// <c>DOC-04</c>, the direction the audit called a documentation gap and the code called nothing: the controllers
/// answer on a set of routes and <c>docs/API.md</c> gave a section to only part of them. Every behaviour a client of
/// the undocumented half has to know — which permission gates the call, which of this server's two pagination
/// envelopes the list comes in, what the <c>201</c> of <c>POST /api/v1/customers</c> puts in <c>Location</c> — was
/// learned by sending requests and reading what came back. The missing sections are written now, and this fact is
/// what keeps the document from drifting again: it reads the host's own route table, so an action added without a
/// section fails a test instead of quietly joining the undocumented set.
/// </summary>
public class EveryEndpointIsDocumentedTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public EveryEndpointIsDocumentedTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void EveryRouteTheControllersAnswerOn_MustHaveASectionInTheDocs()
    {
        var implemented = ApiDoc.ImplementedRoutes(_factory);

        // Not decoration: an ApiExplorer that saw no controllers would make the assertion below pass on an empty set,
        // which is the exact way a document-versus-code fact goes vacuously green.
        implemented.Should().NotBeEmpty("a route table this read found nothing in is not evidence about a document");

        var documented = ApiDoc.DocumentedHeadings().ToHashSet();
        documented.Should().NotBeEmpty("an API.md with no headings would make this fact fire for the wrong reason");

        // Collected, not failed on the first: one run should name every route a client can call and cannot look up.
        var missing = implemented.Where(e => !documented.Contains(e)).ToList();
        missing.Should().BeEmpty(
            $"routes a client can call but has no section to read: {string.Join(", ", missing.Select(m => $"{m.Verb} /{m.Path}"))}");
    }
}
