using System.Net;
using System.Text.Json;
using BooklyHub.Application.Common.Models;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Performance;

/// <summary>
/// PERF-04 was closed on the availability day (`AvailabilityPayloadBoundTests`) and left its residue: the two
/// anonymous catalog reads answered a whole table as a bare array. `GET /api/v1/services` and
/// `GET /api/v1/staff` are `[AllowAnonymous]`, take no page, and had no `Take`, so the body grew with whatever a
/// tenant loaded into its catalog — and a caller that wanted 20 services had to receive and parse all of them.
///
/// These facts pin the fix, which is the envelope the other paged reads on this server already use (`{total, page,
/// pageSize, items}` through the same <see cref="Paging"/> helper, so the ceiling is `MaxPageSize` rather than a new
/// number invented for the public half of the API). The roster is not capped here: `total` tells the caller how much
/// there is, and every row is still reachable by walking pages.
/// </summary>
public class CatalogPagingTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CatalogPagingTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId);

    private async Task<Graph> SeedTenantAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Catalog {slug}", $"catalog-{slug}-{graph.TenantId:N}", "UTC"));
        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
            TenantId = graph.TenantId,
            Name = "Main",
            Address = $"1 {slug} St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task SeedServicesAsync(Graph graph, int count, bool identicalNames = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        for (var i = 0; i < count; i++)
        {
            db.Services.Add(new Service
            {
                TenantId = graph.TenantId,
                Name = identicalNames ? "Ident" : $"Consult {i:D3}",
                Description = "A consult",
                DurationMinutes = 30,
                Price = 100.00m
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedStaffAsync(Graph graph, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        for (var i = 0; i < count; i++)
        {
            var staffId = Guid.NewGuid();
            db.StaffMembers.Add(new Staff
            {
                Id = staffId,
                TenantId = graph.TenantId,
                LocationId = graph.LocationId,
                FirstName = "Ava",
                LastName = $"Doc{i:D3}",
                Email = $"ava{i}-{staffId:N}@catalog.test",
                PhoneNumber = "+963999000000"
            });
        }

        await db.SaveChangesAsync();
    }

    private HttpClient Anonymous(Graph graph)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", graph.TenantId.ToString());
        return client;
    }

    private static readonly JsonSerializerOptions ReaderOptions = new();

    private static async Task<(JsonElement Root, string Body, HttpStatusCode Status)> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(body).RootElement, body, response.StatusCode);
    }

    /// <summary>How many statements one request costs on this host.</summary>
    private async Task<int> QueryCountAsync(Graph graph, string url)
    {
        _factory.QueryInterceptor.Reset();
        await ReadAsync(Anonymous(graph), url);
        return _factory.QueryInterceptor.QueryCount;
    }

    [Fact]
    public async Task ACatalogPage_MustCostTwoStatementsHoweverBigTheRosterIs()
    {
        var graph = await SeedTenantAsync("stmts");
        await SeedStaffAsync(graph, 1);
        await SeedServicesAsync(graph, 1);

        var oneStaff = await QueryCountAsync(graph, "/api/v1/staff?pageSize=100");
        await SeedStaffAsync(graph, 8);
        var nineStaff = await QueryCountAsync(graph, "/api/v1/staff?pageSize=100");
        var services = await QueryCountAsync(graph, "/api/v1/services?pageSize=100");

        // Two: the page and its `total`. The count is the price of stating the size, and it is one statement — the
        // nested `offeredServices` joins into the same read rather than fanning out per member, which is the shape
        // `PERF-02` was about and the reason this route is measured here too (see `AvailabilityBatchingTests` for
        // the day's version of this fact, pinned at eleven).
        nineStaff.Should().Be(oneStaff, "no read on this path may be issued per staff member");
        oneStaff.Should().Be(2, $"the staff directory sent {oneStaff} statements for one member");
        services.Should().Be(2, "the service catalog is a count and a page");
    }

    /// <summary>The ids the envelope's `items` array carries.</summary>
    private static List<Guid> IdsOnPage(JsonElement envelope) =>
        envelope.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

    [Fact]
    public async Task TheAnonymousCatalogReads_MustAnswerAPageNotAWholeTable()
    {
        var graph = await SeedTenantAsync("pages");
        await SeedServicesAsync(graph, 120);
        await SeedStaffAsync(graph, 40);

        var client = Anonymous(graph);

        var services = await ReadAsync(client, "/api/v1/services");
        services.Status.Should().Be(HttpStatusCode.OK, services.Body);
        services.Root.GetProperty("total").GetInt32().Should().Be(120, "the caller still has to be told how big the catalog is");
        services.Root.GetProperty("items").GetArrayLength().Should().Be(Paging.DefaultPageSize,
            "a route that pages by default must not answer 120 rows to the caller that asked for nothing");
        services.Root.GetProperty("pageSize").GetInt32().Should().Be(Paging.DefaultPageSize);
        services.Root.GetProperty("page").GetInt32().Should().Be(1);

        var staff = await ReadAsync(client, "/api/v1/staff");
        staff.Status.Should().Be(HttpStatusCode.OK, staff.Body);
        staff.Root.GetProperty("total").GetInt32().Should().Be(40);
        staff.Root.GetProperty("items").GetArrayLength().Should().Be(Paging.DefaultPageSize);

        // The bare array is gone, and with it the shape a client had no way to size.
        services.Root.ValueKind.Should().Be(JsonValueKind.Object,
            "the envelope is the fix; an array would mean this fact is reading the old route");
    }

    [Fact]
    public async Task AnIllegalAsk_MustBecomeTheDefaultPageRatherThanAFaultOrTheWholeTable()
    {
        var graph = await SeedTenantAsync("clamps");
        await SeedServicesAsync(graph, 120);

        var client = Anonymous(graph);

        // The three asks PAG-01 measured on the other paged routes: below the first page, over the ceiling, past any
        // real offset. A catalog route that had no paging had none of this clamping either.
        foreach (var url in new[]
                 {
                     "/api/v1/services?page=0",
                     "/api/v1/services?pageSize=5000",
                     "/api/v1/services?page=2147483647",
                     "/api/v1/services?page=0&pageSize=0"
                 })
        {
            var response = await ReadAsync(client, url);
            response.Status.Should().Be(HttpStatusCode.OK, $"{url} -> {response.Body}");
            response.Root.GetProperty("pageSize").GetInt32().Should().Be(Paging.DefaultPageSize,
                $"{url}: an out-of-range size falls back to the default, not to the ceiling and not to the table");
            response.Root.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(Paging.MaxPageSize,
                $"{url}: no ask may make the route answer more than the ceiling");
        }

        var pastTheEnd = await ReadAsync(client, "/api/v1/services?page=999");
        pastTheEnd.Root.GetProperty("items").GetArrayLength().Should().Be(0);
        pastTheEnd.Root.GetProperty("total").GetInt32().Should().Be(120,
            "an empty page is the honest content of a page past the end, and the total says so");
    }

    [Fact]
    public async Task WalkingThePages_MustReachEveryRowExactlyOnce()
    {
        var graph = await SeedTenantAsync("walk");
        // Six services sharing one name: OrderBy(Name) alone is not a total order, and OFFSET/FETCH over a tie is
        // where a page boundary can repeat a row and skip another. Recorded honestly: with the tiebreaker removed
        // this walk still passed on LocalDB, whose plan happened to be stable over six rows — the ORDER BY fact
        // below is what pins the tiebreaker, and this one is what a client walking pages would notice.
        await SeedServicesAsync(graph, 6, identicalNames: true);

        var client = Anonymous(graph);

        var firstPage = await ReadAsync(client, "/api/v1/services?pageSize=1");
        firstPage.Status.Should().Be(HttpStatusCode.OK, firstPage.Body);

        var seen = new List<Guid>();
        for (var page = 1; page <= 6; page++)
        {
            var (_, body, status) = await ReadAsync(client, $"/api/v1/services?pageSize=1&page={page}");
            status.Should().Be(HttpStatusCode.OK, body);
            seen.Add(JsonDocument.Parse(body).RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        }

        var wholeCatalog = await ReadAsync(client, "/api/v1/services?pageSize=100");
        var all = IdsOnPage(wholeCatalog.Root);

        all.Should().HaveCount(6, "the single-page read is the set the walk has to reproduce");
        seen.Distinct().Should().HaveCount(6,
            $"a tied ORDER BY hands page boundaries out unpredictably: the walk saw {seen.Count} ids with {seen.Distinct().Count()} distinct");
        seen.Should().BeEquivalentTo(all);
    }

    [Fact]
    public async Task TheOrderOfACatalogPage_MustBeWhatTheServerActuallySent()
    {
        var graph = await SeedTenantAsync("order");
        await SeedServicesAsync(graph, 3);
        await SeedStaffAsync(graph, 3);

        _factory.QueryInterceptor.Reset();
        var client = Anonymous(graph);
        await ReadAsync(client, "/api/v1/services?pageSize=100");
        await ReadAsync(client, "/api/v1/staff?pageSize=100");
        var commands = _factory.QueryInterceptor.ExecutedCommands.ToList();
        _factory.QueryInterceptor.Reset();

        var services = PagingOrderBys(commands, "Services");
        var staff = PagingOrderBys(commands, "StaffMembers");

        services.Should().NotBeEmpty("this fact reads the paging query's own ORDER BY, so the read has to find one");
        staff.Should().NotBeEmpty();

        services.Should().OnlyContain(t => t.EndsWith(", [s].[Id]", StringComparison.Ordinal),
            $"Name is not unique, so the OFFSET is standing on a tie: {string.Join(" | ", services)}");
        staff.Should().OnlyContain(t => t.EndsWith(", [s].[Id]", StringComparison.Ordinal),
            $"LastName and FirstName are not unique either: {string.Join(" | ", staff)}");
    }

    /// <summary>
    /// The ORDER BY lines sitting directly above an OFFSET/FETCH in a batch that reads <paramref name="table"/> — the
    /// ordering the page boundary is actually cut on. A slice to the end of the text is not enough: the same batch
    /// carries join predicates and an outer ORDER BY, so "the key appears somewhere after ORDER BY" reads green on a
    /// query sorting by nothing but the name. Measured that way and corrected — dropping the tiebreaker left the
    /// first draft of this fact passing, because the category join's `[s2].[Id]` sat downstream of the clause. The
    /// staff projection joins Services too, so its page boundary is checked on both sides: stricter, not looser.
    /// </summary>
    private static List<string> PagingOrderBys(IEnumerable<string> commandTexts, string table)
    {
        var found = new List<string>();

        foreach (var text in commandTexts.Where(t => t.Contains($"FROM [{table}]", StringComparison.Ordinal)))
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].TrimStart().StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
                    lines[i + 1].TrimStart().StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(lines[i].Trim());
                }
            }
        }

        return found;
    }
}
