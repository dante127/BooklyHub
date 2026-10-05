using System.Net;
using System.Text.Json;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Pagination;

/// <summary>
/// PAG-01: <c>GET /api/v1/customers?page=0</c> answered 500, <c>?pageSize=-5</c> answered 500,
/// <c>?pageSize=100000</c> was honoured as an instruction to load the tenant's whole book, and on all four paged
/// routes <c>?page=2147483647</c> answered 500 because the offset is the product of two numbers a caller picks and
/// that product is an <c>int</c>. Two of the four routes clamped nothing; the other two clamped the factors and
/// multiplied them anyway.
/// </summary>
/// <remarks>
/// Each fact walks all four routes and collects, so one run names every route that still trusts a caller's
/// numbers rather than stopping at the first. The routes answer through two different envelopes
/// (<c>PaginatedList</c> and the flat <c>{ total, page, pageSize, items }</c>), so the reads go by candidate field
/// name — the assertion is about what the server did, not about which shape it used to say so.
/// </remarks>
public class PagingBoundsTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly string[] Routes =
    [
        "customers",
        "reviews",
        "appointments",
        "payments/outstanding-visits"
    ];

    private readonly BooklyHubWebApplicationFactory _factory;

    public PagingBoundsTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client() => _factory.CreateClientForTenant(Guid.NewGuid(), Roles.TenantOwner);

    private sealed record Answer(int Status, JsonElement Body)
    {
        /// <summary>The page the server applied, under whichever of this server's two envelope names carries it.</summary>
        public int? Page() => Read("pageNumber", "page");

        public int? PageSize() => Read("pageSize");

        public int ItemCount() =>
            Body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength()
                : -1;

        private int? Read(params string[] names)
        {
            foreach (var name in names)
            {
                if (Body.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
            }

            return null;
        }
    }

    private static async Task<Answer> ReadAsync(HttpResponseMessage response) =>
        new((int)response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());

    [Fact]
    public async Task ANonPositivePage_MustLandOnTheFirstPageRatherThanFaultTheServer()
    {
        using var client = Client();

        var broken = new List<string>();
        foreach (var route in Routes)
        {
            foreach (var ask in new[] { "page=0", "page=-1" })
            {
                var answer = await ReadAsync(await client.GetAsync($"/api/v1/{route}?{ask}&pageSize=5"));

                // The two questions this fact has to keep apart: a refusal (the 500 this route used to give) and an
                // empty page, which would also be the shape of a clamp that threw the ask away instead of answering
                // it. Both field name and item count are checked, so neither passes for the other.
                if (answer.Status != 200 || answer.Page() != 1 || answer.ItemCount() < 0)
                    broken.Add($"/api/v1/{route}?{ask} -> {(answer.Status == 200 ? $"applied page {answer.Page()}" : $"{answer.Status}")}");
            }
        }

        broken.Should().BeEmpty(
            $"a caller that loses count is asking for the first page, and got: {string.Join(" | ", broken)}");
    }

    [Fact]
    public async Task ANonPositivePageSize_MustBecomeTheDefaultRatherThanFaultTheServer()
    {
        using var client = Client();

        var broken = new List<string>();
        foreach (var route in Routes)
        foreach (var ask in new[] { "pageSize=0", "pageSize=-5" })
        {
            var answer = await ReadAsync(await client.GetAsync($"/api/v1/{route}?page=1&{ask}"));
            if (answer.Status != 200 || answer.PageSize() != Paging.DefaultPageSize)
                broken.Add($"/api/v1/{route}?{ask} -> {(answer.Status == 200 ? $"applied size {answer.PageSize()}" : $"{answer.Status}")}");
        }

        broken.Should().BeEmpty(
            $"an illegal page size is one question with one answer on every route, and got: {string.Join(" | ", broken)}");
    }

    [Fact]
    public async Task AnOversizedPageSize_MustNotBeHonouredAsAnInstructionToLoadTheTable()
    {
        using var client = Client();

        var broken = new List<string>();
        foreach (var route in Routes)
        {
            var answer = await ReadAsync(await client.GetAsync($"/api/v1/{route}?page=1&pageSize=100000"));
            if (answer.Status != 200 || answer.PageSize() != Paging.DefaultPageSize)
                broken.Add($"/api/v1/{route} -> {(answer.Status == 200 ? $"applied size {answer.PageSize()}" : $"{answer.Status}")}");
        }

        broken.Should().BeEmpty(
            $"the ceiling is the point of having one, and got: {string.Join(" | ", broken)}");
    }

    [Fact]
    public async Task APageWhoseOffsetOverflowsAnInt_MustBeAnEmptyPageRatherThanA500()
    {
        using var client = Client();

        var broken = new List<string>();
        foreach (var route in Routes)
        {
            // int.MaxValue as a page is legal on its own; only (page - 1) * pageSize is not, which is why the two
            // routes that already clamped their page still answered 500 here.
            var answer = await ReadAsync(await client.GetAsync($"/api/v1/{route}?page={int.MaxValue}&pageSize=20"));
            if (answer.Status != 200 || answer.ItemCount() != 0)
                broken.Add($"/api/v1/{route} -> {(answer.Status == 200 ? $"{answer.ItemCount()} items" : $"{answer.Status}")}");
        }

        broken.Should().BeEmpty(
            $"a page past the end of the book is an empty page, and got: {string.Join(" | ", broken)}");
    }

    [Fact]
    public async Task ClampedPaging_MustStillWalkThroughRealRows()
    {
        // The opposite failure: a clamp implemented as "always offset 0" answers every fact above and never pages
        // anything. Three rows in one tenant, two per page, so page 1 and page 2 cannot be the same answer.
        var tenantId = Guid.NewGuid();
        await SeedCustomersAsync(tenantId, 3);

        using var client = _factory.CreateClientForTenant(tenantId, Roles.TenantOwner);

        var first = await ReadAsync(await client.GetAsync("/api/v1/customers?page=1&pageSize=2"));
        var second = await ReadAsync(await client.GetAsync("/api/v1/customers?page=2&pageSize=2"));
        var past = await ReadAsync(await client.GetAsync("/api/v1/customers?page=9&pageSize=2"));
        var clamped = await ReadAsync(await client.GetAsync("/api/v1/customers?page=0&pageSize=2"));

        first.ItemCount().Should().Be(2);
        second.ItemCount().Should().Be(1);
        past.ItemCount().Should().Be(0, "page 9 of three rows is a real page, and it is empty");
        clamped.ItemCount().Should().Be(first.ItemCount(), "a lost count lands on the first page, and the first page has content");
        clamped.Page().Should().Be(1);
        first.Body.GetProperty("total").GetInt32().Should().Be(3);
    }

    private async Task SeedCustomersAsync(Guid tenantId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(tenantId, "Paged Clinic", $"paged-{tenantId:N}", "UTC"));
        for (var i = 1; i <= count; i++)
        {
            db.Customers.Add(new Customer
            {
                TenantId = tenantId,
                FirstName = $"Row{i:00}",
                LastName = "Paged",
                Email = $"row{i:00}@{tenantId:N}.test"
            });
        }

        await db.SaveChangesAsync();
    }
}
