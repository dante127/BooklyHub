using System.Net;
using System.Net.Http.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Concurrency;

public class ConcurrentBookingTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public ConcurrentBookingTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ConcurrentBookingRequests_ForExactSameSlot_MustAllowOnlyOneSuccessAndAllOthersConflict()
    {
        // 1. Setup seed data in test database
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        // Target booking date: Tomorrow 10:00 UTC
        var targetStartAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(10);
        var targetDayOfWeek = targetStartAtUtc.DayOfWeek;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenant = new Tenant(tenantId, "Concurrency Test Clinic", "concurrency-test", "UTC");
            tenant.Settings = new TenantSetting(tenantId)
            {
                MinBookingNoticeMinutes = 10,
                MaxAdvanceBookingDays = 30,
                SlotIntervalMinutes = 30
            };
            db.Tenants.Add(tenant);

            var location = new Location
            {
                Id = locationId,
                TenantId = tenantId,
                Name = "Test Location",
                Address = "123 Test St",
                City = "Test City",
                Country = "US",
                TimeZoneId = "UTC"
            };
            db.Locations.Add(location);

            var service = new Service
            {
                Id = serviceId,
                TenantId = tenantId,
                Name = "General Consultation",
                DurationMinutes = 30,
                Price = 100.00m,
                BufferBeforeMinutes = 0,
                BufferAfterMinutes = 0
            };
            db.Services.Add(service);

            var staff = new Staff
            {
                Id = staffId,
                TenantId = tenantId,
                LocationId = locationId,
                FirstName = "Dr. John",
                LastName = "Locke",
                Email = "john.locke@test.com"
            };
            staff.StaffServices.Add(new StaffService { TenantId = tenantId, StaffId = staffId, ServiceId = serviceId });

            var workingHour = new WorkingHour
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StaffId = staffId,
                LocationId = locationId,
                DayOfWeek = targetDayOfWeek,
                IsWorkingDay = true
            };
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
            staff.WorkingHours.Add(workingHour);

            db.StaffMembers.Add(staff);

            var customer = new Customer
            {
                Id = customerId,
                TenantId = tenantId,
                FirstName = "Jack",
                LastName = "Shephard",
                Email = "jack@test.com"
            };
            db.Customers.Add(customer);

            await db.SaveChangesAsync();
        }

        // 2. Prepare 5 simultaneous clients trying to book the EXACT SAME slot
        const int concurrentRequestsCount = 5;
        var clients = Enumerable.Range(0, concurrentRequestsCount)
            .Select(_ => _factory.CreateClientForTenant(tenantId, Roles.Staff))
            .ToList();

        var requestPayload = new AppointmentsController.BookAppointmentRequest(
            LocationId: locationId,
            ServiceId: serviceId,
            StaffId: staffId,
            CustomerId: customerId,
            StartAtUtc: targetStartAtUtc,
            Notes: "Concurrent test booking");

        // 3. Fire all requests concurrently
        var bookingTasks = clients.Select(async client =>
        {
            var response = await client.PostAsJsonAsync("/api/v1/appointments", requestPayload);
            return response.StatusCode;
        });

        var results = await Task.WhenAll(bookingTasks);

        // 4. Assertions:
        // Exactly ONE request must succeed (201 Created or 200 OK)
        var successCount = results.Count(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK);
        var conflictCount = results.Count(s => s == HttpStatusCode.Conflict);

        successCount.Should().Be(1, because: "only the first transaction to acquire the slot reservation can succeed");
        conflictCount.Should().Be(concurrentRequestsCount - 1, because: "all overlapping concurrent attempts must be rejected with 409 Conflict");

        // 5. Verify database integrity
        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var appointmentsInDb = await db.Appointments
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && a.StaffId == staffId && a.StartAtUtc == targetStartAtUtc)
                .ToListAsync();

            appointmentsInDb.Should().HaveCount(1, because: "the database must never contain duplicate bookings for the same staff at the same time");
            appointmentsInDb.First().Status.Should().Be(AppointmentStatus.Confirmed);
        }
    }
}
