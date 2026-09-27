using System.Net;
using System.Net.Http.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

public class CrossTenantSecurityTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CrossTenantSecurityTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CrossTenantAccess_TenantACannotAccessTenantBAppointment()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        Guid tenantBApptId;
        Guid tenantAApptId;

        // Seed data for Tenant A and Tenant B
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenantA = new Tenant(tenantAId, "Tenant A", "tenant-a", "UTC");
            var tenantB = new Tenant(tenantBId, "Tenant B", "tenant-b", "UTC");
            db.Tenants.AddRange(tenantA, tenantB);

            var locB = new Location { Id = Guid.NewGuid(), TenantId = tenantBId, Name = "Loc B", Address = "B", City = "B", Country = "US" };
            var srvB = new Service { Id = Guid.NewGuid(), TenantId = tenantBId, Name = "Srv B", DurationMinutes = 30, Price = 50 };
            var staffB = new Staff { Id = Guid.NewGuid(), TenantId = tenantBId, LocationId = locB.Id, FirstName = "Staff", LastName = "B", Email = "b@b.com" };
            var custB = new Customer { Id = Guid.NewGuid(), TenantId = tenantBId, FirstName = "Cust", LastName = "B", Email = "custb@b.com" };

            db.Locations.Add(locB);
            db.Services.Add(srvB);
            db.StaffMembers.Add(staffB);
            db.Customers.Add(custB);

            var apptB = Appointment.Create(
                tenantBId,
                locB.Id,
                srvB.Id,
                staffB.Id,
                custB.Id,
                DateTime.UtcNow.AddDays(1),
                DateTime.UtcNow.AddDays(1).AddMinutes(30),
                30,
                50.00m);
            apptB.TransitionTo(AppointmentStatus.Confirmed);

            db.Appointments.Add(apptB);
            tenantBApptId = apptB.Id;

            // Tenant A's own appointment: proves the filter resolves to the caller's tenant rather
            // than to whatever tenant the (cached) model happened to be built for.
            var locA = new Location { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Loc A", Address = "A", City = "A", Country = "US" };
            var srvA = new Service { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Srv A", DurationMinutes = 30, Price = 50 };
            var staffA = new Staff { Id = Guid.NewGuid(), TenantId = tenantAId, LocationId = locA.Id, FirstName = "Staff", LastName = "A", Email = "a@a.com" };
            var custA = new Customer { Id = Guid.NewGuid(), TenantId = tenantAId, FirstName = "Cust", LastName = "A", Email = "custa@a.com" };

            db.Locations.Add(locA);
            db.Services.Add(srvA);
            db.StaffMembers.Add(staffA);
            db.Customers.Add(custA);

            var apptA = Appointment.Create(
                tenantAId,
                locA.Id,
                srvA.Id,
                staffA.Id,
                custA.Id,
                DateTime.UtcNow.AddDays(1),
                DateTime.UtcNow.AddDays(1).AddMinutes(30),
                30,
                50.00m);
            apptA.TransitionTo(AppointmentStatus.Confirmed);

            db.Appointments.Add(apptA);
            tenantAApptId = apptA.Id;

            await db.SaveChangesAsync();
        }

        // Authenticate as Tenant A
        var clientA = _factory.CreateClientForTenant(tenantAId, Roles.TenantAdmin);

        // Positive control: Tenant A must see its own appointment
        var ownResponse = await clientA.GetAsync($"/api/v1/appointments/{tenantAApptId}");
        ownResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Attempt 1: Directly request Tenant B's appointment by ID
        var response = await clientA.GetAsync($"/api/v1/appointments/{tenantBApptId}");

        // Must return 404 Not Found (it does not exist in Tenant A's realm)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Attempt 2: Spoof header X-Tenant-Id to Tenant B
        clientA.DefaultRequestHeaders.Remove("X-Tenant-Id");
        clientA.DefaultRequestHeaders.Add("X-Tenant-Id", tenantBId.ToString());

        var spoofResponse = await clientA.GetAsync($"/api/v1/appointments/{tenantBApptId}");

        // Must STILL return 404 because server derives tenant from authenticated JWT claim, ignoring spoofed header!
        spoofResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PrivilegeEscalation_StaffRoleCannotAccessReports_ShouldReturnForbidden()
    {
        var tenantId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tenant = new Tenant(tenantId, "Privilege Test", "privilege-test", "UTC");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        // Create client with Staff role (which lacks reports.read permission)
        var staffClient = _factory.CreateClientForTenant(tenantId, Roles.Staff);

        var response = await staffClient.GetAsync("/api/v1/reports/dashboard");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnauthenticatedAccess_ToProtectedEndpoint_ShouldReturnUnauthorized()
    {
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/v1/appointments");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
