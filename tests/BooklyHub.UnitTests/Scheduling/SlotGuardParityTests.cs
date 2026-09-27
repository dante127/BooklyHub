using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Resources;
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

/// <summary>
/// The booking guard must accept every slot the availability preview offers, and reject the calendar
/// rules the preview applies: shifts, breaks, approved absence, holidays, notice horizon and resources.
/// </summary>
public class SlotGuardParityTests
{
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private sealed class Scenario
    {
        public required string DbName { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid LocationId { get; init; }
        public required Guid ServiceId { get; init; }
        public required Guid StaffId { get; init; }
        public required DateOnly Date { get; init; }

        public AvailabilityService CreateService(IClock clock, ICurrentUser currentUser)
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId, isPlatformAdmin: false);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(DbName)
                .Options;

            return new AvailabilityService(new ApplicationDbContext(options, tenantContext, currentUser, clock), clock);
        }

        public DateTime Utc(int hour, int minute = 0) => UtcOn(Date, hour, minute);

        public DateTime UtcOn(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute));
    }

    private async Task<Scenario> SeedAsync(
        Action<ApplicationDbContext, Scenario>? configure = null,
        int bufferBeforeMinutes = 0,
        int bufferAfterMinutes = 0,
        int minNoticeMinutes = 60)
    {
        var scenario = new Scenario
        {
            DbName = Guid.NewGuid().ToString(),
            TenantId = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            ServiceId = Guid.NewGuid(),
            StaffId = Guid.NewGuid(),
            Date = new DateOnly(2026, 10, 5)
        };

        var adminContext = new TenantContext();
        adminContext.SetTenant(scenario.TenantId, isPlatformAdmin: true);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(scenario.DbName)
            .Options;

        await using var db = new ApplicationDbContext(options, adminContext, _currentUser, _clock);

        var tenant = new Tenant(scenario.TenantId, "Guard Clinic", "guard-clinic", "UTC");
        tenant.Settings = new TenantSetting(scenario.TenantId)
        {
            MinBookingNoticeMinutes = minNoticeMinutes,
            MaxAdvanceBookingDays = 30,
            SlotIntervalMinutes = 30
        };
        db.Tenants.Add(tenant);

        db.Locations.Add(new Location
        {
            Id = scenario.LocationId,
            TenantId = scenario.TenantId,
            Name = "Main",
            Address = "1 Main St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        db.Services.Add(new Service
        {
            Id = scenario.ServiceId,
            TenantId = scenario.TenantId,
            Name = "Consultation",
            DurationMinutes = 30,
            Price = 90.00m,
            BufferBeforeMinutes = bufferBeforeMinutes,
            BufferAfterMinutes = bufferAfterMinutes
        });

        var staff = new Staff
        {
            Id = scenario.StaffId,
            TenantId = scenario.TenantId,
            LocationId = scenario.LocationId,
            FirstName = "Dana",
            LastName = "Quinn",
            Email = "dana@clinic.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = scenario.TenantId,
            StaffId = scenario.StaffId,
            ServiceId = scenario.ServiceId
        });

        // Monday: 09:00-17:00 with a 12:00-13:00 break.
        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = scenario.TenantId,
            StaffId = scenario.StaffId,
            LocationId = scenario.LocationId,
            DayOfWeek = DayOfWeek.Monday,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), isBreak: false));
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0), isBreak: true));
        staff.WorkingHours.Add(workingHour);

        db.StaffMembers.Add(staff);

        configure?.Invoke(db, scenario);

        await db.SaveChangesAsync();

        return scenario;
    }

    private void SetClock(DateTime utc) => _clock.UtcNow.Returns(utc);

    private static Appointment BookedAppointment(Scenario scenario, int startHour, int startMinute = 0, int minutes = 30)
    {
        var start = scenario.Utc(startHour, startMinute);
        var appointment = Appointment.Create(
            scenario.TenantId,
            scenario.LocationId,
            scenario.ServiceId,
            scenario.StaffId,
            Guid.NewGuid(),
            start,
            start.AddMinutes(minutes),
            minutes,
            90.00m);
        appointment.TransitionTo(AppointmentStatus.Confirmed);
        return appointment;
    }

    [Fact]
    public async Task CheckSlot_EverySlotThePreviewOffers_MustBeAcceptedByTheGuard()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync((db, s) => db.Appointments.Add(BookedAppointment(s, 10)));

        var service = scenario.CreateService(_clock, _currentUser);
        var preview = await service.GetAvailabilityAsync(new GetAvailabilityQuery(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, scenario.Date));

        preview.Slots.Should().NotBeEmpty("the preview must offer something for parity to mean anything");
        preview.Slots.Should().Contain(s => s.StartAtUtc == scenario.Utc(9));
        preview.Slots.Should().NotContain(s => s.StartAtUtc == scenario.Utc(10), "an appointment occupies 10:00");

        var unexpected = new List<string>();

        foreach (var slot in preview.Slots)
        {
            var result = await service.CheckSlotAsync(
                scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
                slot.StartAtUtc, slot.EndAtUtc);

            if (!result.IsAvailable)
            {
                unexpected.Add($"{slot.StartAtUtc:HH:mm} -> {result.Reason}");
            }
        }

        unexpected.Should().BeEmpty("every previewed slot must pass the booking guard");
    }

    [Theory]
    [InlineData(8, 0, false)]
    [InlineData(17, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(13, 30, true)]
    public async Task CheckSlot_OutsideShiftOrDuringBreak_MatchesExpectedOutcome(int hour, int minute, bool expectAvailable)
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync();
        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(hour, minute), scenario.Utc(hour, minute).AddMinutes(30));

        // 13:30 sits in the afternoon shift and must be bookable; the others are outside it.
        if (expectAvailable)
        {
            result.IsAvailable.Should().BeTrue();
            return;
        }

        result.Reason.Should().Be(SlotUnavailableReason.StaffNotAvailable);
    }

    [Fact]
    public async Task CheckSlot_DuringApprovedTimeOff_MustBeRejected()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        var scenario = await SeedAsync((db, s) => db.AvailabilityExceptions.Add(new AvailabilityException
        {
            Id = Guid.NewGuid(),
            TenantId = s.TenantId,
            StaffId = s.StaffId,
            StartDateTimeUtc = s.Utc(14),
            EndDateTimeUtc = s.Utc(16),
            IsAvailable = false,
            Reason = "Sick leave"
        }));

        var service = scenario.CreateService(_clock, _currentUser);

        var inside = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(14), scenario.Utc(14, 30));
        inside.Reason.Should().Be(SlotUnavailableReason.StaffNotAvailable);

        var after = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(16), scenario.Utc(16, 30));
        after.IsAvailable.Should().BeTrue();

        var preview = await service.GetAvailabilityAsync(new GetAvailabilityQuery(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, scenario.Date));
        preview.Slots.Should().NotContain(s => s.StartAtUtc == scenario.Utc(14) || s.StartAtUtc == scenario.Utc(15));
    }

    [Fact]
    public async Task CheckSlot_OnHoliday_MustReportClosed()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        var scenario = await SeedAsync((db, s) => db.Holidays.Add(new Holiday
        {
            Id = Guid.NewGuid(),
            TenantId = s.TenantId,
            LocationId = s.LocationId,
            Name = "Clinic closed",
            Date = s.Date
        }));

        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));

        result.Reason.Should().Be(SlotUnavailableReason.Closed);
    }

    [Fact]
    public async Task CheckSlot_WithinMinimumNotice_MustReportOutsideBookingWindow()
    {
        // Clock 09:00 with 60 minutes notice: 09:00 is inside the shift but too soon to book.
        SetClock(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync();
        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));

        result.Reason.Should().Be(SlotUnavailableReason.OutsideBookingWindow);

        var preview = await service.GetAvailabilityAsync(new GetAvailabilityQuery(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, scenario.Date));
        preview.Slots.Should().NotContain(s => s.StartAtUtc < scenario.Utc(10));
    }

    [Fact]
    public async Task CheckSlot_OverlappingAppointment_MustReportStaffBusy()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync((db, s) => db.Appointments.Add(BookedAppointment(s, 10)));
        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10), scenario.Utc(10, 30));

        result.Reason.Should().Be(SlotUnavailableReason.StaffBusy);
    }

    [Fact]
    public async Task CheckSlot_IgnoringPreviousAppointmentBuffer_MustReportStaffBusy()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        // 15 minutes of buffer either side; an appointment runs 10:00-10:30, so 10:30 is not free
        // until 10:45.
        var scenario = await SeedAsync(
            (db, s) => db.Appointments.Add(BookedAppointment(s, 10)),
            bufferBeforeMinutes: 15,
            bufferAfterMinutes: 15);

        var service = scenario.CreateService(_clock, _currentUser);

        var tooSoon = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10, 30), scenario.Utc(11));
        tooSoon.Reason.Should().Be(SlotUnavailableReason.StaffBusy);

        var atEdge = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10, 45), scenario.Utc(11, 15));
        atEdge.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task CheckSlot_RescheduledAppointmentItself_MustNotBlockTheNewSlot()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        Appointment? seeded = null;
        var scenario = await SeedAsync((db, s) =>
        {
            seeded = BookedAppointment(s, 10);
            db.Appointments.Add(seeded);
        });

        var service = scenario.CreateService(_clock, _currentUser);

        var blocked = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10), scenario.Utc(10, 30));
        blocked.IsAvailable.Should().BeFalse();

        var excluded = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10), scenario.Utc(10, 30),
            excludeAppointmentId: seeded!.Id);
        excluded.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task CheckSlot_WithoutRequiredResources_MustReportResourceUnavailable()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        var groupId = Guid.NewGuid();

        var scenario = await SeedAsync((db, s) =>
        {
            db.ResourceGroups.Add(new ResourceGroup { Id = groupId, TenantId = s.TenantId, Name = "Rooms" });

            db.Resources.Add(new Resource
            {
                Id = Guid.NewGuid(),
                TenantId = s.TenantId,
                ResourceGroupId = groupId,
                LocationId = s.LocationId,
                Name = "Room 1"
            });

            var service = db.Services.Local.Single(x => x.Id == s.ServiceId);
            service.ResourceRequirements.Add(new ServiceResourceRequirement
            {
                TenantId = s.TenantId,
                ServiceId = s.ServiceId,
                ResourceGroupId = groupId,
                QuantityRequired = 2
            });
        });

        var service2 = scenario.CreateService(_clock, _currentUser);

        var result = await service2.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));

        result.Reason.Should().Be(SlotUnavailableReason.ResourceUnavailable);

        var preview = await service2.GetAvailabilityAsync(new GetAvailabilityQuery(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, scenario.Date));
        preview.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckSlots_BulkForm_MustReachTheSameVerdictsAsOneSlotAtATime()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        var scenario = await SeedAsync((db, s) =>
        {
            db.Appointments.Add(BookedAppointment(s, 10));
            db.Holidays.Add(new Holiday
            {
                Id = Guid.NewGuid(),
                TenantId = s.TenantId,
                LocationId = s.LocationId,
                Name = "Clinic closed",
                Date = s.Date.AddDays(7)
            });
        });

        var service = scenario.CreateService(_clock, _currentUser);

        var monday = scenario.Date;
        var candidates = new List<SlotCandidate>
        {
            new(scenario.Utc(9), scenario.Utc(9, 30)),                                       // free
            new(scenario.Utc(10), scenario.Utc(10, 30)),                                     // taken
            new(scenario.UtcOn(monday.AddDays(7), 9), scenario.UtcOn(monday.AddDays(7), 9, 30)), // holiday
            new(scenario.UtcOn(monday.AddDays(1), 8), scenario.UtcOn(monday.AddDays(1), 8, 30))   // Tuesday, no roster row
        };

        var bulk = await service.CheckSlotsAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, candidates);

        bulk.Should().HaveCount(candidates.Count);
        bulk[0].IsAvailable.Should().BeTrue();
        bulk[1].Reason.Should().Be(SlotUnavailableReason.StaffBusy);
        bulk[2].Reason.Should().Be(SlotUnavailableReason.Closed);
        bulk[3].Reason.Should().Be(SlotUnavailableReason.StaffNotAvailable);

        for (var i = 0; i < candidates.Count; i++)
        {
            var single = await service.CheckSlotAsync(
                scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
                candidates[i].StartAtUtc, candidates[i].EndAtUtc);

            single.Reason.Should().Be(bulk[i].Reason, $"slot {candidates[i].StartAtUtc:yyyy-MM-dd HH:mm} must be judged identically in bulk");
            single.ResourceIds.Should().Equal(bulk[i].ResourceIds, $"slot {candidates[i].StartAtUtc:yyyy-MM-dd HH:mm} must be allocated the same resources in bulk");
        }
    }

    [Fact]
    public async Task CheckSlot_StaffNotAssignedToService_MustBeRejected()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        var otherServiceId = Guid.NewGuid();
        var scenario = await SeedAsync((db, s) => db.Services.Add(new Service
        {
            Id = otherServiceId,
            TenantId = s.TenantId,
            Name = "X-Ray review",
            DurationMinutes = 30
        }));

        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, otherServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));

        result.Reason.Should().Be(SlotUnavailableReason.StaffNotAvailable);
    }

    [Fact]
    public async Task CheckSlot_InactiveLocationOrMissingTenant_MustReportClosed()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync();
        var service = scenario.CreateService(_clock, _currentUser);

        var unknownLocation = await service.CheckSlotAsync(
            scenario.TenantId, Guid.NewGuid(), scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));
        unknownLocation.Reason.Should().Be(SlotUnavailableReason.Closed);

        var unknownTenant = await service.CheckSlotAsync(
            Guid.NewGuid(), scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));
        unknownTenant.Reason.Should().Be(SlotUnavailableReason.Closed);
    }

    private static Guid SeedRoom(
        ApplicationDbContext db,
        Scenario scenario,
        string name,
        int quantityRequired = 1)
    {
        var groupId = Guid.NewGuid();
        var roomId = Guid.NewGuid();

        db.ResourceGroups.Add(new ResourceGroup { Id = groupId, TenantId = scenario.TenantId, Name = "Rooms" });
        db.Resources.Add(new Resource
        {
            Id = roomId,
            TenantId = scenario.TenantId,
            ResourceGroupId = groupId,
            LocationId = scenario.LocationId,
            Name = name
        });

        db.Services.Local.Single(x => x.Id == scenario.ServiceId).ResourceRequirements.Add(new ServiceResourceRequirement
        {
            TenantId = scenario.TenantId,
            ServiceId = scenario.ServiceId,
            ResourceGroupId = groupId,
            QuantityRequired = quantityRequired
        });

        return roomId;
    }

    private static Appointment AppointmentHoldingResource(
        ApplicationDbContext db,
        Scenario scenario,
        int startHour,
        Guid resourceId,
        AppointmentStatus status = AppointmentStatus.Confirmed,
        Guid? staffId = null)
    {
        var start = scenario.Utc(startHour);

        var appointment = Appointment.Create(
            scenario.TenantId,
            scenario.LocationId,
            scenario.ServiceId,
            staffId ?? scenario.StaffId,
            Guid.NewGuid(),
            start,
            start.AddMinutes(30),
            30,
            90.00m);

        appointment.TransitionTo(status);
        appointment.AppointmentResources.Add(new AppointmentResource
        {
            TenantId = scenario.TenantId,
            AppointmentId = appointment.Id,
            ResourceId = resourceId
        });

        db.Appointments.Add(appointment);
        return appointment;
    }

    [Fact]
    public async Task CheckSlot_RescheduleIntoOverlappingSlot_MustNotBeBlockedByItsOwnRoom()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        Guid roomId = Guid.Empty;
        Appointment? moving = null;

        var scenario = await SeedAsync((db, s) =>
        {
            roomId = SeedRoom(db, s, "Room 1");
            moving = AppointmentHoldingResource(db, s, 10, roomId);
        });

        var service = scenario.CreateService(_clock, _currentUser);

        // The location has one room and this appointment holds it 10:00-10:30. Moving it to 10:15 keeps
        // the room, so the overlap it creates with itself must not be read as somebody else's booking.
        // The staff pass already honoured the exclusion, so drop it from the resource pass and this same
        // call comes back ResourceUnavailable: the appointment competes with itself for its own room.
        var moved = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10, 15), scenario.Utc(10, 45),
            excludeAppointmentId: moving!.Id);

        moved.IsAvailable.Should().BeTrue(moved.Message);
        moved.ResourceIds.Should().Equal(roomId);
    }

    [Fact]
    public async Task CheckSlot_MustHandBackTheResourcesTheBookingHasToPersist()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        Guid roomA = Guid.Empty;
        Guid roomB = Guid.Empty;
        var groupId = Guid.NewGuid();

        var scenario = await SeedAsync((db, s) =>
        {
            db.ResourceGroups.Add(new ResourceGroup { Id = groupId, TenantId = s.TenantId, Name = "Rooms" });

            roomA = Guid.NewGuid();
            roomB = Guid.NewGuid();

            db.Resources.Add(new Resource { Id = roomA, TenantId = s.TenantId, ResourceGroupId = groupId, LocationId = s.LocationId, Name = "Room A" });
            db.Resources.Add(new Resource { Id = roomB, TenantId = s.TenantId, ResourceGroupId = groupId, LocationId = s.LocationId, Name = "Room B" });

            db.Services.Local.Single(x => x.Id == s.ServiceId).ResourceRequirements.Add(new ServiceResourceRequirement
            {
                TenantId = s.TenantId,
                ServiceId = s.ServiceId,
                ResourceGroupId = groupId,
                QuantityRequired = 2
            });
        });

        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(9), scenario.Utc(9, 30));

        result.IsAvailable.Should().BeTrue(result.Message);
        result.ResourceIds.Should().BeEquivalentTo(new[] { roomA, roomB });
    }

    [Fact]
    public async Task CheckSlot_ResourceBusyForAnotherStaff_MustBeRefusedForThisOne()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        Guid roomId = Guid.Empty;

        var scenario = await SeedAsync((db, s) =>
        {
            roomId = SeedRoom(db, s, "Room 1");

            // A different staff member holds the location's only room at 10:00. This staff member is
            // free, so only the location-wide resource rule can refuse the slot.
            AppointmentHoldingResource(db, s, 10, roomId, staffId: Guid.NewGuid());
        });

        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10), scenario.Utc(10, 30));

        result.Reason.Should().Be(SlotUnavailableReason.ResourceUnavailable);

        var preview = await service.GetAvailabilityAsync(new GetAvailabilityQuery(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId, scenario.Date));
        preview.Slots.Should().NotContain(slot => slot.StartAtUtc == scenario.Utc(10),
            "the preview must not offer a slot whose room is already taken");
    }

    [Fact]
    public async Task CheckSlot_ResourceReleasedByCancelledAppointment_MustBeBookable()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));

        Guid roomId = Guid.Empty;

        var scenario = await SeedAsync((db, s) =>
        {
            roomId = SeedRoom(db, s, "Room 1");
            AppointmentHoldingResource(db, s, 10, roomId, AppointmentStatus.Cancelled);
        });

        var service = scenario.CreateService(_clock, _currentUser);

        var result = await service.CheckSlotAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            scenario.Utc(10), scenario.Utc(10, 30));

        result.IsAvailable.Should().BeTrue(result.Message);
        result.ResourceIds.Should().Equal(roomId);
    }

    [Fact]
    public async Task CheckSlots_OverlappingCandidates_MustOnlyReserveTheSlotThatComesFirst()
    {
        SetClock(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc));
        var scenario = await SeedAsync();
        var service = scenario.CreateService(_clock, _currentUser);

        var checks = await service.CheckSlotsAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            [
                new SlotCandidate(scenario.Utc(14), scenario.Utc(14, 30)),
                new SlotCandidate(scenario.Utc(14, 15), scenario.Utc(14, 45))
            ]);

        checks[0].IsAvailable.Should().BeTrue(checks[0].Message);
        checks[1].Reason.Should().Be(SlotUnavailableReason.StaffBusy,
            "one request must never book the same staff member twice over");

        // The batch is judged in time order but answered by input index.
        var reversed = await service.CheckSlotsAsync(
            scenario.TenantId, scenario.LocationId, scenario.ServiceId, scenario.StaffId,
            [
                new SlotCandidate(scenario.Utc(14, 15), scenario.Utc(14, 45)),
                new SlotCandidate(scenario.Utc(14), scenario.Utc(14, 30))
            ]);

        reversed[1].IsAvailable.Should().BeTrue(reversed[1].Message);
        reversed[0].Reason.Should().Be(SlotUnavailableReason.StaffBusy);
    }
}
