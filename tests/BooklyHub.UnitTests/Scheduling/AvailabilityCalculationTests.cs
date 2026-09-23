using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace BooklyHub.UnitTests.Scheduling;

public class AvailabilityCalculationTests
{
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private ApplicationDbContext CreateDbContext(ITenantContext tenantContext, string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new ApplicationDbContext(options, tenantContext, _currentUser, _clock);
    }

    [Fact]
    public async Task GetAvailability_ShouldRespectWorkingHoursBreaksAndExistingAppointments()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();

        // Fixed test clock: 2026-10-05 06:00:00 UTC (Monday)
        var fixedClock = new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);
        _clock.UtcNow.Returns(fixedClock);

        var adminContext = new TenantContext();
        adminContext.SetTenant(tenantId, isPlatformAdmin: true);

        using (var db = CreateDbContext(adminContext, dbName))
        {
            var tenant = new Tenant(tenantId, "Dental Clinic", "dental", "UTC");
            tenant.Settings = new TenantSetting(tenantId)
            {
                MinBookingNoticeMinutes = 60, // 1 hr notice
                MaxAdvanceBookingDays = 30,
                SlotIntervalMinutes = 30
            };
            db.Tenants.Add(tenant);

            var location = new Location
            {
                Id = locationId,
                TenantId = tenantId,
                Name = "Main Clinic",
                Address = "123 Main St",
                City = "New York",
                Country = "US",
                TimeZoneId = "UTC"
            };
            db.Locations.Add(location);

            var service = new Service
            {
                Id = serviceId,
                TenantId = tenantId,
                Name = "Routine Cleaning",
                DurationMinutes = 30,
                Price = 120.00m,
                BufferBeforeMinutes = 0,
                BufferAfterMinutes = 0
            };
            db.Services.Add(service);

            var staff = new Staff
            {
                Id = staffId,
                TenantId = tenantId,
                LocationId = locationId,
                FirstName = "Dr. Sarah",
                LastName = "Connor",
                Email = "sarah@clinic.com"
            };
            staff.StaffServices.Add(new StaffService
            {
                TenantId = tenantId,
                StaffId = staffId,
                ServiceId = serviceId
            });

            // Working hours: Monday 09:00 - 17:00 UTC with Lunch break 12:00 - 13:00 UTC
            var workingHour = new WorkingHour
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StaffId = staffId,
                LocationId = locationId,
                DayOfWeek = DayOfWeek.Monday,
                IsWorkingDay = true
            };
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), isBreak: false));
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0), isBreak: true));
            staff.WorkingHours.Add(workingHour);

            db.StaffMembers.Add(staff);

            // Existing appointment on 2026-10-05 from 10:00 to 10:30 UTC
            var existingAppt = Appointment.Create(
                tenantId,
                locationId,
                serviceId,
                staffId,
                Guid.NewGuid(),
                new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 5, 10, 30, 0, DateTimeKind.Utc),
                30,
                120.00m);
            existingAppt.TransitionTo(AppointmentStatus.Confirmed);

            db.Appointments.Add(existingAppt);

            await db.SaveChangesAsync();
        }

        // Query availability as tenant user
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId, isPlatformAdmin: false);

        using (var db = CreateDbContext(tenantContext, dbName))
        {
            var availabilityService = new AvailabilityService(db, _clock);

            var query = new GetAvailabilityQuery(
                tenantId,
                locationId,
                serviceId,
                staffId,
                new DateOnly(2026, 10, 5));

            var result = await availabilityService.GetAvailabilityAsync(query);

            result.IsOpen.Should().BeTrue();
            result.Slots.Should().NotBeEmpty();

            // 09:00 slot must exist
            result.Slots.Should().Contain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));

            // 09:30 slot must exist
            result.Slots.Should().Contain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc));

            // 10:00 slot must NOT exist (occupied by appointment)
            result.Slots.Should().NotContain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc));

            // 10:30 slot must exist
            result.Slots.Should().Contain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 10, 30, 0, DateTimeKind.Utc));

            // 12:00 and 12:30 slots must NOT exist (lunch break)
            result.Slots.Should().NotContain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc));
            result.Slots.Should().NotContain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc));

            // 13:00 slot must exist
            result.Slots.Should().Contain(s => s.StartAtUtc == new DateTime(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc));
        }
    }

    [Fact]
    public async Task GetAvailability_WhenHoliday_ShouldReturnNoSlots()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();

        _clock.UtcNow.Returns(new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc));

        var adminContext = new TenantContext();
        adminContext.SetTenant(tenantId, isPlatformAdmin: true);

        using (var db = CreateDbContext(adminContext, dbName))
        {
            var tenant = new Tenant(tenantId, "Clinic", "clinic", "UTC");
            db.Tenants.Add(tenant);

            var location = new Location
            {
                Id = locationId,
                TenantId = tenantId,
                Name = "Loc",
                Address = "Addr",
                City = "City",
                Country = "US",
                TimeZoneId = "UTC"
            };
            db.Locations.Add(location);

            var service = new Service
            {
                Id = serviceId,
                TenantId = tenantId,
                Name = "Service",
                DurationMinutes = 30
            };
            db.Services.Add(service);

            // Christmas Holiday
            db.Holidays.Add(new Holiday
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                LocationId = locationId,
                Name = "Christmas Day",
                Date = new DateOnly(2026, 12, 25),
                RecurringAnnually = true
            });

            await db.SaveChangesAsync();
        }

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId, isPlatformAdmin: false);

        using (var db = CreateDbContext(tenantContext, dbName))
        {
            var availabilityService = new AvailabilityService(db, _clock);

            var query = new GetAvailabilityQuery(
                tenantId,
                locationId,
                serviceId,
                null,
                new DateOnly(2026, 12, 25));

            var result = await availabilityService.GetAvailabilityAsync(query);

            result.IsOpen.Should().BeFalse();
            result.Slots.Should().BeEmpty();
        }
    }
}
