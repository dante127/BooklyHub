using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Data.Seeding;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Deployments;

/// <summary>
/// What the seeder actually leaves in a client's database.
///
/// This is the one deployment step that writes rows a later run cannot undo, and until now no test had ever
/// called it: <c>BooklyHubWebApplicationFactory</c> sets <c>AutoMigrateAndSeed=false</c>, so every host in the
/// suite migrated an empty schema and skipped the seeding path entirely. These facts run it against SQL Server
/// for real, because the question they answer — does a second run collide with the first — is a question about
/// unique indexes, and no in-memory provider has them.
/// </summary>
public sealed class ProductionSeedTests : IAsyncLifetime
{
    private const string AdminEmail = "ops@example.test";
    private const string AdminPassword = "A-long-enough-seed-password!";

    private sealed class SeedFactory : BooklyHubWebApplicationFactory
    {
    }

    // Not an IClassFixture: each fact seeds its own database and then reads back what the run wrote, so a
    // shared database would let one fact's rows answer another fact's question.
    private readonly SeedFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static IConfiguration SeedConfiguration(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Seed:AdminEmail"] = AdminEmail,
            ["Seed:AdminPassword"] = AdminPassword
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task SeedAsync(IConfiguration configuration)
    {
        using var scope = _factory.Services.CreateScope();

        // Awaited rather than returned: the scope owns the DbContext, and returning the task would dispose it
        // while the seeder is still reading (measured — "Invalid attempt to call FieldCount when reader is
        // closed" on the sentinel query, five facts down at once).
        await DatabaseSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            configuration,
            new CollectingLogger<ProductionSeedTests>(_factory.Logs));
    }

    private async Task<Dictionary<string, int>> CountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return new Dictionary<string, int>
        {
            ["tenants"] = await db.Tenants.IgnoreQueryFilters().CountAsync(),
            ["roles"] = await db.Roles.IgnoreQueryFilters().CountAsync(),
            ["permissions"] = await db.Permissions.IgnoreQueryFilters().CountAsync(),
            ["users"] = await db.Users.IgnoreQueryFilters().CountAsync(),
            ["staff"] = await db.StaffMembers.IgnoreQueryFilters().CountAsync(),
            ["customers"] = await db.Customers.IgnoreQueryFilters().CountAsync()
        };
    }

    [Fact]
    public async Task AFirstRunThatDeclinesDemoTenants_MustStillLeaveAWorkingAdministrator()
    {
        await SeedAsync(SeedConfiguration(new Dictionary<string, string?> { ["Seed:DemoTenants"] = "false" }));

        var counts = await CountsAsync();
        counts["tenants"].Should().Be(0,
            "a client's production tenant table must not arrive pre-populated with three invented clinics — " +
            "there is no tenant delete route, so seeded tenants would be the only ones this server ever has");
        counts["staff"].Should().Be(0);
        counts["customers"].Should().Be(0, "invented patients in a live database are somebody's data, not fixture data");

        counts["roles"].Should().Be(5, "the five system roles are what a sign-in is authorized against");
        counts["permissions"].Should().Be(Permissions.All.Count,
            "the role-to-permission rows the seeder writes reference these by id, so a partial permission set " +
            "would leave a role that grants less than the product says it does");
        counts["users"].Should().Be(1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == AdminEmail);
        hasher.VerifyPassword(AdminPassword, admin.PasswordHash).Should().BeTrue(
            "the operator configured this credential, so the hash has to answer to it — the one thing the " +
            "reference-only run is required to leave behind is a way in");

        (await db.UserRoles.IgnoreQueryFilters()
            .AnyAsync(ur => ur.UserId == admin.Id && ur.Role!.Name == Roles.PlatformAdmin))
            .Should().BeTrue("an administrator with no role is a user who cannot do anything");
    }

    [Fact]
    public async Task AnOperatorWhoSetNothing_MustNotGetDemoTenants()
    {
        // The compose default is the absence of the key, so this is the shape a client deployment actually
        // arrives at, and the only reading that matters.
        await SeedAsync(SeedConfiguration([]));

        var counts = await CountsAsync();

        counts["tenants"].Should().Be(0, "an unset key has to mean the safe half, not the scripted half");
        counts["roles"].Should().Be(5);
        counts["users"].Should().Be(1);
    }

    [Fact]
    public async Task AFirstRunThatAsksForDemoTenants_MustGetTheThreeScriptedClinics()
    {
        await SeedAsync(SeedConfiguration(new Dictionary<string, string?> { ["Seed:DemoTenants"] = "true" }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var slugs = await db.Tenants.IgnoreQueryFilters().OrderBy(t => t.Name)
            .Select(t => t.Slug).ToListAsync();

        slugs.Should().BeEquivalentTo(["apex-dental", "luxe-salon", "pulse-fitness"],
            "the development database is what a new reviewer sees first, and its calendar, staff roster and " +
            "customer list are the demo block's — declining to write them would leave that reviewer an empty product");

        (await db.StaffMembers.IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(0);
        (await db.Customers.IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ASecondRunAfterAReferenceOnlyFirstRun_MustNotWriteTheReferenceRowsTwice()
    {
        await SeedAsync(SeedConfiguration(new Dictionary<string, string?> { ["Seed:DemoTenants"] = "false" }));
        var afterFirst = await CountsAsync();

        // The realistic accident: an operator runs the migrator once with the default, then flips
        // SEED_DEMO_TENANTS=true and brings the stack up again. Judging "already seeded" by the tenant table —
        // which is what this method did until the demo half became opt-in — reads "empty" here and re-inserts
        // roles and permissions the first run already wrote. Measured against the old sentinel, that is not a
        // quiet no-op: `Violation of PRIMARY KEY constraint 'PK_Permissions'. The duplicate key value is
        // (appointments.cancel)`, thrown from inside a one-shot migrator container.
        await SeedAsync(SeedConfiguration(new Dictionary<string, string?> { ["Seed:DemoTenants"] = "true" }));

        var afterSecond = await CountsAsync();

        afterSecond.Should().Equal(afterFirst,
            "a second run has to be a no-op rather than an exception with no hint that the answer was to leave " +
            "the seed settings alone; the migrator is a container that exits, and a stack trace is the only " +
            "thing an operator sees before `depends_on: service_completed_successfully` holds the API down");
        afterSecond["tenants"].Should().Be(0,
            "and the demo half does not become available by asking after the fact — the sentinel had already " +
            "returned, which is the documented cost of an opt-in that is read once");
        _factory.Logs.Containing("already seeded").Should().ContainSingle(
            "the run has to say why it wrote nothing, or an operator who flipped the flag gets silence");
    }

    [Fact]
    public async Task ASeedWithNoAdministratorEmail_MustRefuseAndWriteNothing()
    {
        var act = async () => await SeedAsync(SeedConfiguration(new Dictionary<string, string?>
        {
            ["Seed:AdminEmail"] = ""
        }));

        var failure = await act.Should().ThrowAsync<InvalidOperationException>(
            "an empty Seed__AdminEmail in a compose file produces a database with roles and no way to sign in, " +
            "which is a deployment nobody can finish without knowing how to insert rows by hand");

        failure.And.Message.Should().Contain("Seed:AdminEmail",
            "the key is the thing the operator has to go and set");

        var counts = await CountsAsync();
        counts["roles"].Should().Be(0, "refusing has to happen before the first write, or the refused run leaves a half-seeded database");
        counts["permissions"].Should().Be(0);
        counts["users"].Should().Be(0);
    }
}
