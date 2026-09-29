using System.Net.Http.Json;
using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Reports.Queries;
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

namespace BooklyHub.IntegrationTests.Performance;

public class NPlusOneQueryTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public NPlusOneQueryTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SearchAppointments_WithManyRecords_MustExecuteOnlyTwoSqlQueries_NoNPlusOne()
    {
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        // 1. Seed 25 appointments with associated entities
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenant = new Tenant(tenantId, "N1 Test Tenant", "n1-tenant", "UTC");
            db.Tenants.Add(tenant);

            var location = new Location
            {
                Id = locationId,
                TenantId = tenantId,
                Name = "N1 Location",
                Address = "Street 1",
                City = "City",
                Country = "US"
            };
            db.Locations.Add(location);

            var service = new Service
            {
                Id = serviceId,
                TenantId = tenantId,
                Name = "Dental Cleaning",
                DurationMinutes = 30,
                Price = 100.00m
            };
            db.Services.Add(service);

            var staff = new Staff
            {
                Id = staffId,
                TenantId = tenantId,
                LocationId = locationId,
                FirstName = "Doctor",
                LastName = "Who",
                Email = "doctor@who.com"
            };
            db.StaffMembers.Add(staff);

            for (var i = 0; i < 25; i++)
            {
                var customer = new Customer
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    FirstName = $"Customer_{i}",
                    LastName = $"Surname_{i}",
                    Email = $"cust_{i}@test.com"
                };
                db.Customers.Add(customer);

                var start = DateTime.UtcNow.Date.AddDays(i + 1).AddHours(9);
                var appt = Appointment.Create(
                    tenantId,
                    locationId,
                    serviceId,
                    staffId,
                    customer.Id,
                    start,
                    start.AddMinutes(30),
                    30,
                    100.00m);
                appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

                db.Appointments.Add(appt);
            }

            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientForTenant(tenantId, Roles.Staff);

        // 2. Reset query counter interceptor
        _factory.QueryInterceptor.Reset();

        // 3. Execute search endpoint
        var response = await client.GetAsync("/api/v1/appointments?page=1&pageSize=20");
        response.EnsureSuccessStatusCode();

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var result = await response.Content.ReadFromJsonAsync<PaginatedList<AppointmentDto>>(jsonOptions);
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(20);

        // 4. Assert query count:
        // Must be exactly 2 queries:
        // Query 1: SELECT COUNT(1) FROM Appointments WHERE TenantId = @tenantId
        // Query 2: SELECT a.Id, a.LocationId, l.Name, ... FROM Appointments a JOIN ... ORDER BY ... OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY
        // Zero N+1 queries!
        var totalQueries = _factory.QueryInterceptor.QueryCount;

        totalQueries.Should().BeLessThanOrEqualTo(2,
            because: $"fetching a page of appointments should use at most 2 SQL queries (Count + Paginated Project). Observed: {totalQueries}");
    }

    [Fact]
    public async Task DashboardReport_MustExecuteSetBasedSqlAggregations_NoNPlusOne()
    {
        var tenantId = Guid.NewGuid();

        // Seed tenant
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tenant = new Tenant(tenantId, "Report Test Tenant", "report-tenant", "UTC");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientForTenant(tenantId, Roles.TenantAdmin);

        _factory.QueryInterceptor.Reset();

        var response = await client.GetAsync("/api/v1/reports/dashboard");
        response.EnsureSuccessStatusCode();

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var report = await response.Content.ReadFromJsonAsync<DashboardReportDto>(jsonOptions);
        report.Should().NotBeNull();

        // Dashboard aggregates should execute in at most 4 set-based SQL queries
        var totalQueries = _factory.QueryInterceptor.QueryCount;
        totalQueries.Should().BeLessThanOrEqualTo(4,
            because: $"dashboard reports must use pure SQL aggregations rather than loading entity rows. Observed: {totalQueries}");
    }
}
