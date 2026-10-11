using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Data.Seeding;
using BooklyHub.IntegrationTests.Infrastructure;
using BooklyHub.IntegrationTests.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Tenancy;

/// <summary>
/// <c>ONB-01</c>: a clinic and its staff arrive over HTTP.
///
/// Until these routes existed, the only writer of a <c>Tenant</c> row in <c>src</c> and the only writer of a
/// <c>User</c> row were the same file — <c>DatabaseSeeder</c>, which writes one platform administrator and, if asked,
/// three invented clinics. A deployed database therefore had one credential and zero tenants, and "give the clinic a
/// receptionist" meant <c>INSERT</c> statements run by hand: outside every permission in the product, outside every
/// validator, and with no answer about what a role is even supposed to grant.
/// </summary>
/// <remarks>
/// Each fact gets its own migrated and seeded database, and the seeding is <c>DatabaseSeeder</c> itself rather than
/// hand-written role rows. That is what makes the central claim here a measurement: a provisioned owner's token
/// carries the permissions this deployment's <c>RolePermission</c> rows hold, so a fact that ran on a minted token
/// would still pass on a database whose reference pass was broken.
///
/// Sign-ins go over the wire for the same reason, and they are the reason each fact has its own host: the <c>auth</c>
/// tier is ten permitted requests a minute per address, and a file that shares one host across fifteen sign-ins is a
/// file that fails for the wrong reason.
/// </remarks>
public class OnboardingTests : IAsyncLifetime
{
    private const string AdminEmail = "ops@onboarding.test";
    private const string AdminPassword = "the-seed-admin-password-12";
    private const string OwnerPassword = "a-chosen-password-1234";

    private sealed class OnboardingFactory : BooklyHubWebApplicationFactory
    {
    }

    private readonly OnboardingFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.InitializeAsync();

        using var scope = _factory.Services.CreateScope();
        await DatabaseSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminEmail"] = AdminEmail,
                ["Seed:AdminPassword"] = AdminPassword,
                ["Seed:DemoTenants"] = "false"
            }).Build(),
            new CollectingLogger<OnboardingTests>(_factory.Logs));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ---------------------------------------------------------------------------------------------
    // Wire
    // ---------------------------------------------------------------------------------------------

    private sealed record SignedIn(HttpClient Http, string RefreshToken, JsonElement User);

    private static string NewSlug() => $"clinic-{Guid.NewGuid():N}";

    private static string NewEmail(string tag) => $"{tag}-{Guid.NewGuid():N}@onboarding.test";

    private static object ProvisionPayload(
        string slug,
        string ownerEmail,
        string name = "Clinic",
        string timeZoneId = "UTC",
        string ownerPassword = OwnerPassword) => new
        {
            Name = name,
            Slug = slug,
            TimeZoneId = timeZoneId,
            Plan = (string?)null,
            OwnerEmail = ownerEmail,
            OwnerPassword = ownerPassword,
            OwnerFirstName = "Olivia",
            OwnerLastName = "Owner",
            OwnerPhoneNumber = "+1-555-0100"
        };

    private static object AccountPayload(string email, IReadOnlyList<string> roles, string password = OwnerPassword) => new
    {
        Email = email,
        Password = password,
        FirstName = "Nora",
        LastName = "Reception",
        PhoneNumber = (string?)null,
        Roles = roles
    };

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient http, string url, object payload)
    {
        var response = await http.PostAsJsonAsync(url, payload);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> PutAsync(HttpClient http, string url, object payload)
    {
        var response = await http.PutAsJsonAsync(url, payload);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A refusal the exception boundary wrote, with its content type and its body both checked.</summary>
    private static async Task<JsonElement> RefusalAsync(HttpResponseMessage response, int status)
    {
        var problem = await ProblemEnvelope.ReadAsync(response, status);
        response.StatusCode.Should().Be((HttpStatusCode)status, problem.GetRawText());
        return problem;
    }

    /// <summary>The named field of a validation 400, whose <c>errors</c> map is keyed by the property refused.</summary>
    private static string ValidationMessage(JsonElement problem, string field)
    {
        problem.GetProperty("errors").TryGetProperty(field, out var messages).Should().BeTrue(
            $"the 400 was expected to refuse '{field}':\n{problem.GetRawText()}");
        return messages[0].GetString()!;
    }

    private static string Rule(JsonElement problem) => problem.GetProperty("rule").GetString()!;

    /// <summary>Signs in over HTTP, so every token these facts call with is one the server minted.</summary>
    private async Task<SignedIn> SignInAsync(string email, string password)
    {
        var http = _factory.CreateClient();
        var (status, body) = await PostAsync(http, "/api/v1/auth/login", new { Email = email, Password = password });
        status.Should().Be(HttpStatusCode.OK, body);

        var root = Body(body);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", root.GetProperty("accessToken").GetString());

        return new SignedIn(http, root.GetProperty("refreshToken").GetString()!, root.GetProperty("user"));
    }

    private async Task<HttpClient> PlatformAdminAsync() => (await SignInAsync(AdminEmail, AdminPassword)).Http;

    /// <summary>
    /// Provisions a clinic as the platform administrator and signs its owner in, returning both.
    /// </summary>
    private async Task<(JsonElement Tenant, SignedIn Owner)> ProvisionAsync(HttpClient admin, string? slug = null, string? ownerEmail = null)
    {
        slug ??= NewSlug();
        ownerEmail ??= NewEmail("owner");

        var (status, body) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload(slug, ownerEmail));
        status.Should().Be(HttpStatusCode.Created, body);

        var created = Body(body);
        created.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);

        return (created, await SignInAsync(ownerEmail, OwnerPassword));
    }

    private async Task<Guid> HireAsync(SignedIn owner, string email, IReadOnlyList<string> roles, string password = OwnerPassword)
    {
        var (status, body) = await PostAsync(owner.Http, "/api/v1/users", AccountPayload(email, roles, password));
        status.Should().Be(HttpStatusCode.Created, body);
        return Body(body).GetProperty("id").GetGuid();
    }

    private async Task<Dictionary<string, int>> CountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return new Dictionary<string, int>
        {
            ["tenants"] = await db.Tenants.IgnoreQueryFilters().CountAsync(),
            ["users"] = await db.Users.IgnoreQueryFilters().CountAsync(),
            ["orphanAccounts"] = await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == Guid.Empty)
        };
    }

    /// <summary>
    /// The bookable half of a clinic, written by hand. Not because a route could not do it — the calendar routes
    /// exist — but because nothing here yet creates a location, a service, a staff member or a working hour, which
    /// is the gap these new routes expose and the next finding after this one.
    /// </summary>
    private async Task<(Guid LocationId, Guid ServiceId, Guid StaffId, DateOnly Day)> SeedBookableGraphAsync(Guid tenantId)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));

        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Locations.Add(new Location
        {
            Id = locationId,
            TenantId = tenantId,
            Name = "Front",
            Address = "1 Front St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        db.Services.Add(new Service
        {
            Id = serviceId,
            TenantId = tenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        var staff = new Staff
        {
            Id = staffId,
            TenantId = tenantId,
            LocationId = locationId,
            FirstName = "Ava",
            LastName = "Clinic",
            Email = NewEmail("staff")
        };
        staff.StaffServices.Add(new StaffService { TenantId = tenantId, StaffId = staffId, ServiceId = serviceId });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StaffId = staffId,
            LocationId = locationId,
            DayOfWeek = day.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);

        db.StaffMembers.Add(staff);
        await db.SaveChangesAsync();

        return (locationId, serviceId, staffId, day);
    }

    private async Task<JsonElement> PublicPreviewAsync(Guid tenantId, (Guid LocationId, Guid ServiceId, Guid StaffId, DateOnly Day) graph)
    {
        var http = _factory.CreateClient();
        var response = await http.GetAsync(
            $"/api/v1/availability?tenantId={tenantId}&locationId={graph.LocationId}&serviceId={graph.ServiceId}" +
            $"&staffId={graph.StaffId}&date={graph.Day}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return Body(body);
    }

    // ---------------------------------------------------------------------------------------------
    // The finding's own claim: the clinic and its staff arrive over HTTP, and their authority is the
    // database's seeded role, not a copy of the code's list.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProvisionedTenant_MustSignInItsOwnerWithTheSeededOwnerPermissions()
    {
        var admin = await PlatformAdminAsync();

        // What the caller typed is not what gets stored, and the response has to say which of the two it is
        // describing — a client that later reads a lowercase slug out of the list would otherwise think it was
        // somebody else's clinic.
        var typed = $"  {NewSlug().ToUpperInvariant()}  ";
        var ownerEmail = NewEmail("owner");
        var (status, body) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload(typed, ownerEmail));
        status.Should().Be(HttpStatusCode.Created, body);

        var created = Body(body);
        created.GetProperty("slug").GetString().Should().Be(typed.Trim().ToLowerInvariant());
        created.GetProperty("isActive").GetBoolean().Should().BeTrue();
        created.GetProperty("timeZoneId").GetString().Should().Be("UTC");
        created.GetProperty("plan").GetString().Should().Be("Standard", "the request named no plan");
        created.GetProperty("settings").GetProperty("slotIntervalMinutes").GetInt32().Should().BeGreaterThan(0,
            "a clinic with no settings row answers every reader with a fallback, which is the shape EXP-01 removed");
        created.GetProperty("owner").GetProperty("email").GetString().Should().Be(ownerEmail);
        created.GetProperty("owner").GetProperty("roles").EnumerateArray().Select(r => r.GetString())
            .Should().BeEquivalentTo([Roles.TenantOwner], "the owner's role is not a parameter of this route");
        created.ToString().Should().NotContain("passwordHash", "the response echoes the account, never the credential");

        var owner = await SignInAsync(ownerEmail, OwnerPassword);
        owner.User.GetProperty("roles").EnumerateArray().Select(r => r.GetString())
            .Should().BeEquivalentTo([Roles.TenantOwner]);
        owner.User.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().BeEquivalentTo(Permissions.GetDefaultPermissionsForRole(Roles.TenantOwner),
                "these claims are read out of this database's RolePermission rows at sign-in, so passing here is " +
                "the measurement that the reference pass wrote them — an owner that signs in holding nothing is the " +
                "failure this finding is about");

        var roster = await owner.Http.GetAsync("/api/v1/users");
        var rosterBody = await roster.Content.ReadAsStringAsync();
        roster.StatusCode.Should().Be(HttpStatusCode.OK, rosterBody);
        Body(rosterBody).GetProperty("total").GetInt32().Should().Be(1, "the clinic has its owner and nobody else");

        // What the owner may not do: create another clinic. That exclusion is the only thing standing between this
        // route's permission and every tenant owner on the server provisioning tenants.
        Permissions.GetDefaultPermissionsForRole(Roles.TenantOwner).Should().NotContain(Permissions.Tenancy.Manage);
        var (crossedStatus, crossedBody) = await PostAsync(owner.Http, "/api/v1/tenants", ProvisionPayload(NewSlug(), NewEmail("owner")));
        crossedStatus.Should().Be(HttpStatusCode.Forbidden, crossedBody);
    }

    [Fact]
    public async Task AHiredReceptionist_MustSignInWithItsRoleAndBeRefusedToHireAnyone()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var receptionistEmail = NewEmail("reception");
        await HireAsync(owner, receptionistEmail, [Roles.Receptionist]);

        var receptionist = await SignInAsync(receptionistEmail, OwnerPassword);
        var granted = receptionist.User.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        granted.Should().BeEquivalentTo(Permissions.GetDefaultPermissionsForRole(Roles.Receptionist));
        granted.Should().NotBeEmpty(
            "the seeder used to write the Receptionist row with no grants at all — a receptionist that signs in " +
            "holding nothing, which is the first account this route gets asked to create for a client");

        var staffRead = await receptionist.Http.GetAsync("/api/v1/staff");
        staffRead.StatusCode.Should().Be(HttpStatusCode.OK, await staffRead.Content.ReadAsStringAsync());

        var (status, body) = await PostAsync(receptionist.Http, "/api/v1/users", AccountPayload(NewEmail("owner"), [Roles.Receptionist]));
        status.Should().Be(HttpStatusCode.Forbidden, body);
    }

    [Fact]
    public async Task TwoTenants_MustNotSeeOrAddressEachOthersAccounts()
    {
        var admin = await PlatformAdminAsync();
        var (first, firstOwner) = await ProvisionAsync(admin);
        var (second, secondOwner) = await ProvisionAsync(admin);

        Body(await secondOwner.Http.GetStringAsync("/api/v1/users"))
            .GetProperty("total").GetInt32().Should().Be(1, "the roster is the caller's own clinic and nothing else");

        var secondOwnerId = second.GetProperty("owner").GetProperty("id").GetGuid();
        var (offStatus, offBody) = await PutAsync(firstOwner.Http, $"/api/v1/users/{secondOwnerId}/status", new { IsActive = false });
        offStatus.Should().Be(HttpStatusCode.NotFound, offBody);

        var firstRoster = Body(await firstOwner.Http.GetStringAsync("/api/v1/users"));
        firstRoster.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("email").GetString())
            .Should().NotContain(secondOwner.User.GetProperty("email").GetString(),
                "Users carries no tenant query filter, so this predicate on the read is the entire isolation of " +
                "the table — forgetting it would be a roster of every account on the server");

        var (tenantOff, tenantOffBody) = await PutAsync(
            firstOwner.Http, $"/api/v1/tenants/{second.GetProperty("id").GetGuid()}/status", new { IsActive = false });
        tenantOff.Should().Be(HttpStatusCode.Forbidden, tenantOffBody);
    }

    // ---------------------------------------------------------------------------------------------
    // The refusals — each one a way the hand-written INSERT used to answer wrong.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task APlatformAdminWithNoTenantSelected_MustBeRefusedAndWriteNoOrphanAccount()
    {
        var admin = await PlatformAdminAsync();

        // The middleware hands a signed-in platform admin `Guid.Empty` when no X-Tenant-Id names a tenant
        // (TenantResolutionMiddleware.cs:37), and the guard used to test only "has a value". So this request used to
        // be accepted, and the account was stored under tenant 00000000-0000-0000-0000-000000000000 — a row no
        // tenant's read will ever return and no route can delete.
        var response = await admin.PostAsJsonAsync("/api/v1/users", AccountPayload(NewEmail("orphan"), [Roles.Staff]));
        ProblemEnvelope.Field(await RefusalAsync(response, 400), "detail").Should().Be(TenantGuard.MissingTenant);

        var roster = await admin.GetAsync("/api/v1/users");
        roster.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var counts = await CountsAsync();
        counts["orphanAccounts"].Should().Be(0, "the refusal has to happen before the write, not after it");
        counts["users"].Should().Be(1, "and the database still holds only the seeded administrator");
    }

    [Fact]
    public async Task TheAccountsRoute_MustRefuseToMintAPlatformAdmin()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var (status, body) = await PostAsync(owner.Http, "/api/v1/users", AccountPayload(NewEmail("escalate"), [Roles.PlatformAdmin]));
        status.Should().Be(HttpStatusCode.BadRequest, body);
        ValidationMessage(Body(body), "Roles").Should().Contain(Roles.PlatformAdmin);

        // A mixed list is refused whole rather than half-applied: granting the two safe names and dropping the third
        // would be a route quietly doing something different from what was asked.
        var (mixedStatus, mixedBody) = await PostAsync(
            owner.Http, "/api/v1/users", AccountPayload(NewEmail("escalate"), [Roles.Receptionist, Roles.PlatformAdmin]));
        mixedStatus.Should().Be(HttpStatusCode.BadRequest, mixedBody);

        (await CountsAsync())["users"].Should().Be(2, "the administrator plus the owner — neither refusal wrote a row");
    }

    [Fact]
    public async Task ARoleThisDatabaseDoesNotKnow_MustBeRefusedAsABusinessRuleNotACrash()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var (status, body) = await PostAsync(
            owner.Http, "/api/v1/users", AccountPayload(NewEmail("night"), [Roles.Receptionist, "NightShift"]));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        Rule(Body(body)).Should().Be("RoleNotGrantable");

        (await CountsAsync())["users"].Should().Be(2, "one grantable name and one invented one is not a partial hire");
    }

    [Fact]
    public async Task AnAddressAnotherTenantAlreadyHas_MustBeRefusedServerWide()
    {
        var admin = await PlatformAdminAsync();
        var sharedEmail = NewEmail("shared");
        await ProvisionAsync(admin, ownerEmail: sharedEmail);

        var (status, body) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload(NewSlug(), sharedEmail));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        Rule(Body(body)).Should().Be("EmailAlreadyRegistered",
            "a sign-in resolves an address to one account (AuthController.cs:342), so the rule cannot be per tenant " +
            "however the table is organised");

        var counts = await CountsAsync();
        counts["tenants"].Should().Be(1, "the refusal was about the account, and a tenant row left behind would be a clinic nobody owns");
        counts["users"].Should().Be(2);

        var (_, owner) = await ProvisionAsync(admin);
        var (hireStatus, hireBody) = await PostAsync(owner.Http, "/api/v1/users", AccountPayload(sharedEmail, [Roles.Staff]));
        hireStatus.Should().Be(HttpStatusCode.UnprocessableEntity, hireBody);
        Rule(Body(hireBody)).Should().Be("EmailAlreadyRegistered");
    }

    [Fact]
    public async Task AnAddressHeldByADeletedAccount_MustBeRefusedRatherThanTaken()
    {
        var admin = await PlatformAdminAsync();
        var goneEmail = NewEmail("gone");
        var (_, owner) = await ProvisionAsync(admin, ownerEmail: goneEmail);

        // Nothing in src soft-deletes an account — there is no delete route — so the row is put in that state
        // directly. That is also how the state arises in the first place: a hand-written UPDATE, or the cleanup
        // script this repository tells an operator to write for a departed staff member.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var gone = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == goneEmail);
            gone.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        var (status, body) = await PostAsync(owner.Http, "/api/v1/users", AccountPayload(goneEmail, [Roles.Staff]));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        Rule(Body(body)).Should().Be("EmailAlreadyRegistered",
            "IX_Users_Email carries no IsDeleted filter, so the deleted row owns the address. The pre-check reads " +
            "with IgnoreQueryFilters so it agrees: the alternative is the race wording, about a request that had no " +
            "competitor, on a collision the database was holding before the request started");
        Body(body).GetProperty("detail").GetString()!.Should().Contain("already names an account");

        (await CountsAsync())["users"].Should().Be(2, "one refusal, and no second row sharing the address");
    }

    [Fact]
    public async Task AnAccountOutsideTheRoleBound_MustBeRefusedAndWriteNoRow()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var (emptyStatus, emptyBody) = await PostAsync(owner.Http, "/api/v1/users", AccountPayload(NewEmail("none"), []));
        emptyStatus.Should().Be(HttpStatusCode.BadRequest, emptyBody);
        ValidationMessage(Body(emptyBody), "Roles").Should().NotBeNullOrWhiteSpace(
            "an account with no role signs in and can use nothing, which is not an account yet");

        // The bound is OnboardingLimits.MaxRolesPerAccount: four names is one request minting a principal whose
        // claims are the union of everything the caller typed.
        var (overStatus, overBody) = await PostAsync(owner.Http, "/api/v1/users",
            AccountPayload(NewEmail("four"), [Roles.Staff, Roles.Receptionist, Roles.Manager, Roles.Accountant]));
        overStatus.Should().Be(HttpStatusCode.BadRequest, overBody);
        ValidationMessage(Body(overBody), "Roles").Should().Contain("at most 3");

        var (twiceStatus, twiceBody) = await PostAsync(owner.Http, "/api/v1/users",
            AccountPayload(NewEmail("twice"), [Roles.Staff, Roles.Staff]));
        twiceStatus.Should().Be(HttpStatusCode.BadRequest, twiceBody);

        (await CountsAsync())["users"].Should().Be(2, "three refusals and no partial hire");
    }

    [Fact]
    public async Task ConcurrentWritesForOneAddress_MustLeaveOneAccountAndNoServerError()
    {
        var admin = await PlatformAdminAsync();
        var (tenant, owner) = await ProvisionAsync(admin);
        var tenantId = tenant.GetProperty("id").GetGuid();
        var email = NewEmail("race");

        // Four attempts, one address. The read before the write cannot decide this on its own: every request finds
        // the address free before any of them inserts, and the password hash in the middle of the write is a tenth
        // of a second of overlap. The unique index added with this finding is what closes it — and the loser has to
        // be told the address was taken, not handed a 500 for a collision its own request took part in.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PostAsync(_factory.CreateClientForTenant(tenantId, Roles.TenantOwner), "/api/v1/users", AccountPayload(email, [Roles.Staff]))));

        var transcript = string.Join("\n", attempts.Select(a => $"{(int)a.Status} {a.Body}"));
        attempts.Count(a => a.Status == HttpStatusCode.Created).Should().Be(1, transcript);
        attempts.Count(a => (int)a.Status >= 500).Should().Be(0, transcript);
        attempts.Where(a => a.Status != HttpStatusCode.Created).Should().OnlyContain(a => a.Status == HttpStatusCode.UnprocessableEntity, transcript);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == email)).Should().Be(1);
        }
    }

    [Fact]
    public async Task AnIllegalTimeZoneOrSlugOrPassword_MustBeRefusedBeforeAnyRowExists()
    {
        var admin = await PlatformAdminAsync();

        // `ResolveTimeZone` answers UTC for anything it cannot resolve (TimeZoneHelper.cs:30), so a stored typo is
        // not an error — it is a clinic whose whole day of slots sits hours away from its own working hours, in a
        // response that reports nothing. The write has to refuse what the read would silently substitute.
        var (zoneStatus, zoneBody) = await PostAsync(
            admin, "/api/v1/tenants", ProvisionPayload(NewSlug(), NewEmail("owner"), timeZoneId: "Asia/Damascas"));
        zoneStatus.Should().Be(HttpStatusCode.BadRequest, zoneBody);
        ValidationMessage(Body(zoneBody), "TimeZoneId").Should().Contain("time zone");

        var (shortStatus, shortBody) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload("ab", NewEmail("owner")));
        shortStatus.Should().Be(HttpStatusCode.BadRequest, shortBody);

        var (shapeStatus, shapeBody) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload("Apex__Dental", NewEmail("owner")));
        shapeStatus.Should().Be(HttpStatusCode.BadRequest, shapeBody);

        var (weakStatus, weakBody) = await PostAsync(
            admin, "/api/v1/tenants", ProvisionPayload(NewSlug(), NewEmail("owner"), ownerPassword: "short-one"));
        weakStatus.Should().Be(HttpStatusCode.BadRequest, weakBody);
        ValidationMessage(Body(weakBody), "OwnerPassword").Should().Contain("12",
            "the account this route creates is the one that gets locked out, so the password rule has to be the " +
            "same rule the change-password route already enforces");

        var (tooManyStatus, tooManyBody) = await PostAsync(
            admin, "/api/v1/tenants", ProvisionPayload(NewSlug(), NewEmail("owner"), name: new string('n', 201)));
        tooManyStatus.Should().Be(HttpStatusCode.BadRequest, tooManyBody);

        (await CountsAsync())["tenants"].Should().Be(0, "four refused provisions, and no clinic half-written");
    }

    [Fact]
    public async Task ATakenSlug_MustBeRefusedEvenWhenItIsTypedDifferently()
    {
        var admin = await PlatformAdminAsync();
        var slug = NewSlug();
        await ProvisionAsync(admin, slug);

        var (status, body) = await PostAsync(admin, "/api/v1/tenants", ProvisionPayload($"  {slug.ToUpperInvariant()}  ", NewEmail("owner")));
        status.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        Rule(Body(body)).Should().Be("TenantSlugAlreadyTaken",
            "the duplicate check compares the normalized value, because that is what the unique index compares, " +
            "and two spellings of one rule is how ' Apex ' becomes a row the route calls free and the database calls a duplicate");

        // The rule alone does not say which layer refused: the race branch answers the same rule and the same status,
        // so a handler that compared the raw text would still pass the assertion above. The sentence is the observable
        // that distinguishes them — the pre-check names an address the tenant table already holds, the race branch
        // invents a competitor that this request never had.
        Body(body).GetProperty("detail").GetString()!.Should().Contain("already has the slug",
            "'already has the slug' is the pre-check's wording; 'took the slug … while this request was in flight' " +
            "is the collision translation's, and reaching for that sentence here means the read did not see the row " +
            "because it compared an unnormalized value to a normalized column");

        (await CountsAsync())["tenants"].Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------
    // The two columns that had readers and no writer.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantSwitchedOff_MustOfferNoSlotsAndComeBackOn()
    {
        var admin = await PlatformAdminAsync();
        var (tenant, _) = await ProvisionAsync(admin);
        var tenantId = tenant.GetProperty("id").GetGuid();
        var graph = await SeedBookableGraphAsync(tenantId);

        var before = await PublicPreviewAsync(tenantId, graph);
        before.GetProperty("isOpen").GetBoolean().Should().BeTrue("the fixture's own day is open");
        before.GetProperty("slots").GetArrayLength().Should().BeGreaterThan(0);

        var (offStatus, offBody) = await PutAsync(admin, $"/api/v1/tenants/{tenantId}/status", new { IsActive = false });
        offStatus.Should().Be(HttpStatusCode.OK, offBody);
        Body(offBody).GetProperty("isActive").GetBoolean().Should().BeFalse();

        var closed = await PublicPreviewAsync(tenantId, graph);
        closed.GetProperty("isOpen").GetBoolean().Should().BeFalse(
            "this column has been read by the availability guard since before this route existed, so the switch was " +
            "an UPDATE nobody was authorized to run");
        closed.GetProperty("slots").GetArrayLength().Should().Be(0);

        await PutAsync(admin, $"/api/v1/tenants/{tenantId}/status", new { IsActive = true });
        var reopened = await PublicPreviewAsync(tenantId, graph);
        reopened.GetProperty("isOpen").GetBoolean().Should().BeTrue("and the switch is reversible");
    }

    [Fact]
    public async Task AnAccountSwitchedOff_MustStopSigningInAndRefreshAndNobodySwitchesThemselvesOff()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var (selfStatus, selfBody) = await PutAsync(
            owner.Http, $"/api/v1/users/{owner.User.GetProperty("id").GetGuid()}/status", new { IsActive = false });
        selfStatus.Should().Be(HttpStatusCode.UnprocessableEntity, selfBody);
        Rule(Body(selfBody)).Should().Be("CannotDeactivateOwnAccount",
            "nothing in this API switches an account back on, so one misclick would be an outage for one person and " +
            "an UPDATE for whoever has to fix it");

        var receptionistEmail = NewEmail("reception");
        var receptionistId = await HireAsync(owner, receptionistEmail, [Roles.Receptionist]);
        var receptionist = await SignInAsync(receptionistEmail, OwnerPassword);

        var (offStatus, offBody) = await PutAsync(owner.Http, $"/api/v1/users/{receptionistId}/status", new { IsActive = false });
        offStatus.Should().Be(HttpStatusCode.OK, offBody);

        var refresh = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh-token", new { RefreshToken = receptionist.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await refresh.Content.ReadAsStringAsync());

        var login = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = receptionistEmail, Password = OwnerPassword });
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await login.Content.ReadAsStringAsync());

        await PutAsync(owner.Http, $"/api/v1/users/{receptionistId}/status", new { IsActive = true });
        var restored = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = receptionistEmail, Password = OwnerPassword });
        restored.StatusCode.Should().Be(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AStatusRoute_MustReturnNotFoundForAnUnknownIdRatherThanWriteOne()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var (userStatus, userBody) = await PutAsync(owner.Http, $"/api/v1/users/{Guid.NewGuid()}/status", new { IsActive = false });
        userStatus.Should().Be(HttpStatusCode.NotFound, userBody);

        var (tenantStatus, tenantBody) = await PutAsync(admin, $"/api/v1/tenants/{Guid.NewGuid()}/status", new { IsActive = false });
        tenantStatus.Should().Be(HttpStatusCode.NotFound, tenantBody);

        var counts = await CountsAsync();
        counts["users"].Should().Be(2);
        counts["tenants"].Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------
    // The reads that fill a form, and the bounds they answer within.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRolesRoute_MustAnswerThisDeploymentsGrantableSet()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var response = await owner.Http.GetAsync("/api/v1/roles");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var roles = Body(body).GetProperty("roles").EnumerateArray().ToList();
        roles.Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(Roles.All,
                "this is the table read rather than the code's list, which is the only way a caller can learn that " +
                "a deployment seeded by an older build has fewer roles than the current build names");

        foreach (var role in roles)
        {
            var name = role.GetProperty("name").GetString()!;
            role.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
                .Should().BeEquivalentTo(Permissions.GetDefaultPermissionsForRole(name),
                    $"and the grant set offered has to be the one the database holds for {name}");
            role.GetProperty("grantableToATenantAccount").GetBoolean()
                .Should().Be(name != Roles.PlatformAdmin,
                    "the create route refuses this one, so the read says so before a form offers it");
        }
    }

    [Fact]
    public async Task TheTwoNewLists_MustClampAnIllegalPageAndSize()
    {
        var admin = await PlatformAdminAsync();
        var (_, owner) = await ProvisionAsync(admin);

        var users = Body(await owner.Http.GetStringAsync("/api/v1/users?page=0&pageSize=0"));
        users.GetProperty("page").GetInt32().Should().Be(1);
        users.GetProperty("pageSize").GetInt32().Should().Be(Paging.DefaultPageSize);

        var tenants = Body(await admin.GetStringAsync("/api/v1/tenants?page=-3&pageSize=2147483647"));
        tenants.GetProperty("page").GetInt32().Should().Be(1);
        tenants.GetProperty("pageSize").GetInt32().Should().Be(Paging.DefaultPageSize,
            "an illegal ask falls back to the default rather than to the ceiling, which is what PAG-01 settled for " +
            "the four routes that already clamped");

        var past = Body(await owner.Http.GetStringAsync("/api/v1/users?page=2147483647&pageSize=100"));
        past.GetProperty("items").GetArrayLength().Should().Be(0, "a page past the end is an empty page, not a 500");
    }
}
