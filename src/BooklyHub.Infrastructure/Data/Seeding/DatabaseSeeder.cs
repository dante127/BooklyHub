using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.Reviews;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.Data.Seeding;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db, IPasswordHasher hasher, IClock clock, IConfiguration configuration, ILogger logger)
    {
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync())
        {
            logger.LogInformation("Database already seeded. Skipping initial seeding.");
            return;
        }

        var adminEmail = RequireConfiguration(configuration, "Seed:AdminEmail");
        var adminPassword = RequireConfiguration(configuration, "Seed:AdminPassword");

        logger.LogInformation("Starting database seeding for 3 realistic tenants...");

        // Seed Roles & Permissions
        var permissions = Permissions.All.Select(p => new Permission(p, p.Split('.')[0], p)).ToList();
        db.Permissions.AddRange(permissions);

        var platformAdminRole = new Role(Roles.PlatformAdmin, "Global SaaS Administrator", isSystemRole: true);
        var tenantOwnerRole = new Role(Roles.TenantOwner, "Tenant Owner", isSystemRole: true);
        var tenantAdminRole = new Role(Roles.TenantAdmin, "Tenant Administrator", isSystemRole: true);
        var staffRole = new Role(Roles.Staff, "Staff Member", isSystemRole: true);
        var receptionistRole = new Role(Roles.Receptionist, "Receptionist", isSystemRole: true);

        db.Roles.AddRange(platformAdminRole, tenantOwnerRole, tenantAdminRole, staffRole, receptionistRole);

        foreach (var p in permissions)
        {
            platformAdminRole.RolePermissions.Add(new RolePermission { RoleId = platformAdminRole.Id, PermissionId = p.Id });
        }

        foreach (var p in Permissions.GetDefaultPermissionsForRole(Roles.TenantOwner))
        {
            tenantOwnerRole.RolePermissions.Add(new RolePermission { RoleId = tenantOwnerRole.Id, PermissionId = p });
        }

        foreach (var p in Permissions.GetDefaultPermissionsForRole(Roles.Staff))
        {
            staffRole.RolePermissions.Add(new RolePermission { RoleId = staffRole.Id, PermissionId = p });
        }

        // Global Platform Admin User
        var platformAdminUser = new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            FirstName = "Super",
            LastName = "Admin",
            PasswordHash = hasher.HashPassword(adminPassword),
            IsActive = true
        };
        platformAdminUser.UserRoles.Add(new UserRole { UserId = platformAdminUser.Id, RoleId = platformAdminRole.Id });
        db.Users.Add(platformAdminUser);

        // ==========================================
        // TENANT 1: Apex Dental Clinic (Healthcare)
        // ==========================================
        var tenant1 = new Tenant(Guid.NewGuid(), "Apex Dental Clinic", "apex-dental", "America/New_York", "Enterprise");
        tenant1.Settings = new TenantSetting(tenant1.Id) { MinBookingNoticeMinutes = 60, SlotIntervalMinutes = 15 };
        db.Tenants.Add(tenant1);

        var loc1 = new Location
        {
            Id = Guid.NewGuid(),
            TenantId = tenant1.Id,
            Name = "Downtown Dental Center",
            Address = "450 Lexington Ave, Suite 800",
            City = "New York",
            State = "NY",
            PostalCode = "10017",
            Country = "US",
            TimeZoneId = "America/New_York",
            PhoneNumber = "+1-212-555-0190"
        };
        db.Locations.Add(loc1);

        var dentalChairGroup = new ResourceGroup { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "Dental Chairs", Description = "Treatment chairs equipped with delivery systems" };
        var xrayGroup = new ResourceGroup { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "X-Ray Equipment", Description = "Digital panoramic and intraoral radiography" };
        db.ResourceGroups.AddRange(dentalChairGroup, xrayGroup);

        var chair1 = new Resource { Id = Guid.NewGuid(), TenantId = tenant1.Id, ResourceGroupId = dentalChairGroup.Id, LocationId = loc1.Id, Name = "Chair 1 (Operatory A)" };
        var chair2 = new Resource { Id = Guid.NewGuid(), TenantId = tenant1.Id, ResourceGroupId = dentalChairGroup.Id, LocationId = loc1.Id, Name = "Chair 2 (Operatory B)" };
        var xray1 = new Resource { Id = Guid.NewGuid(), TenantId = tenant1.Id, ResourceGroupId = xrayGroup.Id, LocationId = loc1.Id, Name = "Digital X-Ray Unit 1" };
        db.Resources.AddRange(chair1, chair2, xray1);

        var srv1 = new Service { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "Routine Dental Cleaning", DurationMinutes = 45, Price = 120.00m, BufferAfterMinutes = 10 };
        srv1.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant1.Id, ServiceId = srv1.Id, ResourceGroupId = dentalChairGroup.Id, QuantityRequired = 1 });

        var srv2 = new Service { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "Comprehensive Exam & X-Ray", DurationMinutes = 30, Price = 180.00m, BufferAfterMinutes = 5 };
        srv2.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant1.Id, ServiceId = srv2.Id, ResourceGroupId = dentalChairGroup.Id, QuantityRequired = 1 });
        srv2.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant1.Id, ServiceId = srv2.Id, ResourceGroupId = xrayGroup.Id, QuantityRequired = 1 });

        var srv3 = new Service { Id = Guid.NewGuid(), TenantId = tenant1.Id, Name = "Professional Teeth Whitening", DurationMinutes = 60, Price = 350.00m, BufferAfterMinutes = 15 };
        srv3.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant1.Id, ServiceId = srv3.Id, ResourceGroupId = dentalChairGroup.Id, QuantityRequired = 1 });

        db.Services.AddRange(srv1, srv2, srv3);

        var dentist1 = new Staff { Id = Guid.NewGuid(), TenantId = tenant1.Id, LocationId = loc1.Id, FirstName = "Dr. Marcus", LastName = "Vance", Title = "Chief Dentist", Email = "marcus@apexdental.com", ColorHex = "#2563EB" };
        dentist1.StaffServices.Add(new StaffService { TenantId = tenant1.Id, StaffId = dentist1.Id, ServiceId = srv1.Id });
        dentist1.StaffServices.Add(new StaffService { TenantId = tenant1.Id, StaffId = dentist1.Id, ServiceId = srv2.Id });
        dentist1.StaffServices.Add(new StaffService { TenantId = tenant1.Id, StaffId = dentist1.Id, ServiceId = srv3.Id });

        // Dentist Working Hours Mon-Fri 08:30 - 17:30
        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Friday; d++)
        {
            var wh = new WorkingHour { Id = Guid.NewGuid(), TenantId = tenant1.Id, StaffId = dentist1.Id, LocationId = loc1.Id, DayOfWeek = d, IsWorkingDay = true };
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 30, 0), new TimeSpan(17, 30, 0), false));
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(12, 30, 0), new TimeSpan(13, 30, 0), true)); // Lunch
            dentist1.WorkingHours.Add(wh);
        }
        db.StaffMembers.Add(dentist1);

        var cust1 = new Customer { Id = Guid.NewGuid(), TenantId = tenant1.Id, FirstName = "Robert", LastName = "Taylor", Email = "robert.taylor@gmail.com", PhoneNumber = "+1-917-555-0144" };
        var cust2 = new Customer { Id = Guid.NewGuid(), TenantId = tenant1.Id, FirstName = "Emily", LastName = "Watson", Email = "emily.watson@yahoo.com", PhoneNumber = "+1-917-555-0182" };
        db.Customers.AddRange(cust1, cust2);

        // Past Completed Appointment with Review
        var demoStartUtc = clock.UtcNow.AddDays(-2).Date.AddHours(14);
        var appt1 = Appointment.Create(tenant1.Id, loc1.Id, srv1.Id, dentist1.Id, cust1.Id, demoStartUtc, demoStartUtc.AddMinutes(45), 45, 120.00m);
        var seededNowUtc = clock.UtcNow;
        appt1.TransitionTo(AppointmentStatus.Confirmed, seededNowUtc);
        appt1.TransitionTo(AppointmentStatus.CheckedIn, seededNowUtc);
        appt1.TransitionTo(AppointmentStatus.InProgress, seededNowUtc);
        appt1.TransitionTo(AppointmentStatus.Completed, seededNowUtc);
        appt1.AppointmentResources.Add(new AppointmentResource { TenantId = tenant1.Id, AppointmentId = appt1.Id, ResourceId = chair1.Id });
        db.Appointments.Add(appt1);

        var review1 = Review.Create(tenant1.Id, appt1.Id, cust1.Id, dentist1.Id, srv1.Id, 5, "Dr. Vance is incredible! Painless cleaning and very gentle.", "System");
        db.Reviews.Add(review1);

        // ==========================================
        // TENANT 2: Luxe Hair & Beauty Lounge (Salon)
        // ==========================================
        var tenant2 = new Tenant(Guid.NewGuid(), "Luxe Hair & Beauty Lounge", "luxe-salon", "America/Chicago", "Professional");
        tenant2.Settings = new TenantSetting(tenant2.Id) { MinBookingNoticeMinutes = 30, SlotIntervalMinutes = 30 };
        db.Tenants.Add(tenant2);

        var loc2 = new Location
        {
            Id = Guid.NewGuid(),
            TenantId = tenant2.Id,
            Name = "Luxe Magnificent Mile",
            Address = "625 N Michigan Ave",
            City = "Chicago",
            State = "IL",
            PostalCode = "60611",
            Country = "US",
            TimeZoneId = "America/Chicago",
            PhoneNumber = "+1-312-555-0145"
        };
        db.Locations.Add(loc2);

        var chairGroup = new ResourceGroup { Id = Guid.NewGuid(), TenantId = tenant2.Id, Name = "Styling Chairs", Description = "Hydraulic salon styling chairs" };
        db.ResourceGroups.Add(chairGroup);

        var salonChair1 = new Resource { Id = Guid.NewGuid(), TenantId = tenant2.Id, ResourceGroupId = chairGroup.Id, LocationId = loc2.Id, Name = "Chair #1" };
        var salonChair2 = new Resource { Id = Guid.NewGuid(), TenantId = tenant2.Id, ResourceGroupId = chairGroup.Id, LocationId = loc2.Id, Name = "Chair #2" };
        db.Resources.AddRange(salonChair1, salonChair2);

        var hairSrv1 = new Service { Id = Guid.NewGuid(), TenantId = tenant2.Id, Name = "Signature Cut & Blowout", DurationMinutes = 45, Price = 75.00m, BufferAfterMinutes = 5 };
        var hairSrv2 = new Service { Id = Guid.NewGuid(), TenantId = tenant2.Id, Name = "Balayage & Glaze", DurationMinutes = 120, Price = 240.00m, BufferAfterMinutes = 15 };
        hairSrv1.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant2.Id, ServiceId = hairSrv1.Id, ResourceGroupId = chairGroup.Id, QuantityRequired = 1 });
        hairSrv2.ResourceRequirements.Add(new ServiceResourceRequirement { TenantId = tenant2.Id, ServiceId = hairSrv2.Id, ResourceGroupId = chairGroup.Id, QuantityRequired = 1 });
        db.Services.AddRange(hairSrv1, hairSrv2);

        var stylist1 = new Staff { Id = Guid.NewGuid(), TenantId = tenant2.Id, LocationId = loc2.Id, FirstName = "Chloe", LastName = "Bennett", Title = "Master Stylist", Email = "chloe@luxesalon.com", ColorHex = "#EC4899" };
        stylist1.StaffServices.Add(new StaffService { TenantId = tenant2.Id, StaffId = stylist1.Id, ServiceId = hairSrv1.Id });
        stylist1.StaffServices.Add(new StaffService { TenantId = tenant2.Id, StaffId = stylist1.Id, ServiceId = hairSrv2.Id });

        for (var d = DayOfWeek.Tuesday; d <= DayOfWeek.Saturday; d++)
        {
            var wh = new WorkingHour { Id = Guid.NewGuid(), TenantId = tenant2.Id, StaffId = stylist1.Id, LocationId = loc2.Id, DayOfWeek = d, IsWorkingDay = true };
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(10, 0, 0), new TimeSpan(19, 0, 0), false));
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(13, 0, 0), new TimeSpan(14, 0, 0), true));
            stylist1.WorkingHours.Add(wh);
        }
        db.StaffMembers.Add(stylist1);

        var cust3 = new Customer { Id = Guid.NewGuid(), TenantId = tenant2.Id, FirstName = "Olivia", LastName = "Rodriguez", Email = "olivia@example.com", PhoneNumber = "+1-312-555-9011" };
        db.Customers.Add(cust3);

        // ==========================================
        // TENANT 3: Pulse Fitness & Performance
        // ==========================================
        var tenant3 = new Tenant(Guid.NewGuid(), "Pulse Fitness & Performance", "pulse-fitness", "America/Los_Angeles", "Enterprise");
        tenant3.Settings = new TenantSetting(tenant3.Id) { MinBookingNoticeMinutes = 120, SlotIntervalMinutes = 30 };
        db.Tenants.Add(tenant3);

        var loc3 = new Location
        {
            Id = Guid.NewGuid(),
            TenantId = tenant3.Id,
            Name = "Pulse Performance Studio",
            Address = "1200 Olympic Blvd",
            City = "Los Angeles",
            State = "CA",
            PostalCode = "90015",
            Country = "US",
            TimeZoneId = "America/Los_Angeles",
            PhoneNumber = "+1-213-555-0820"
        };
        db.Locations.Add(loc3);

        var fitSrv1 = new Service { Id = Guid.NewGuid(), TenantId = tenant3.Id, Name = "1-on-1 Performance Training", DurationMinutes = 60, Price = 100.00m, BufferAfterMinutes = 10 };
        var fitSrv2 = new Service { Id = Guid.NewGuid(), TenantId = tenant3.Id, Name = "Physiotherapy & Mobility Assessment", DurationMinutes = 45, Price = 140.00m, BufferAfterMinutes = 10 };
        db.Services.AddRange(fitSrv1, fitSrv2);

        var trainer1 = new Staff { Id = Guid.NewGuid(), TenantId = tenant3.Id, LocationId = loc3.Id, FirstName = "Marcus", LastName = "Brody", Title = "Head Performance Coach", Email = "marcus@pulsefitness.com", ColorHex = "#10B981" };
        trainer1.StaffServices.Add(new StaffService { TenantId = tenant3.Id, StaffId = trainer1.Id, ServiceId = fitSrv1.Id });

        for (var d = DayOfWeek.Monday; d <= DayOfWeek.Saturday; d++)
        {
            var wh = new WorkingHour { Id = Guid.NewGuid(), TenantId = tenant3.Id, StaffId = trainer1.Id, LocationId = loc3.Id, DayOfWeek = d, IsWorkingDay = true };
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(6, 0, 0), new TimeSpan(18, 0, 0), false));
            wh.Intervals.Add(new WorkingHourInterval(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0), true));
            trainer1.WorkingHours.Add(wh);
        }
        db.StaffMembers.Add(trainer1);

        var cust4 = new Customer { Id = Guid.NewGuid(), TenantId = tenant3.Id, FirstName = "Lucas", LastName = "Miller", Email = "lucas.miller@gmail.com", PhoneNumber = "+1-213-555-4321" };
        db.Customers.Add(cust4);

        await db.SaveChangesAsync();
        logger.LogInformation("Database seeding completed successfully for 3 tenants.");
    }

    private static string RequireConfiguration(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Configuration key '{key}' is required to seed an administrator account.");
        }

        return value;
    }
}
