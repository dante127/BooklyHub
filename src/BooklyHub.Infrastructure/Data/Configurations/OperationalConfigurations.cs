using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.Reviews;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BooklyHub.Infrastructure.Data.Configurations;

public class OperationalConfigurations :
    IEntityTypeConfiguration<Location>,
    IEntityTypeConfiguration<BusinessHour>,
    IEntityTypeConfiguration<Staff>,
    IEntityTypeConfiguration<StaffService>,
    IEntityTypeConfiguration<ServiceCategory>,
    IEntityTypeConfiguration<Service>,
    IEntityTypeConfiguration<WorkingHour>,
    IEntityTypeConfiguration<WorkingHourInterval>,
    IEntityTypeConfiguration<Holiday>,
    IEntityTypeConfiguration<AvailabilityException>,
    IEntityTypeConfiguration<ResourceGroup>,
    IEntityTypeConfiguration<Resource>,
    IEntityTypeConfiguration<ServiceResourceRequirement>,
    IEntityTypeConfiguration<AppointmentResource>,
    IEntityTypeConfiguration<Customer>,
    IEntityTypeConfiguration<CustomerNote>,
    IEntityTypeConfiguration<Appointment>,
    IEntityTypeConfiguration<AppointmentStatusHistory>,
    IEntityTypeConfiguration<RecurringAppointment>,
    IEntityTypeConfiguration<Payment>,
    IEntityTypeConfiguration<PaymentTransaction>,
    IEntityTypeConfiguration<Refund>,
    IEntityTypeConfiguration<Review>,
    IEntityTypeConfiguration<OutboxMessage>,
    IEntityTypeConfiguration<AuditLog>,
    IEntityTypeConfiguration<IdempotencyRecord>,
    IEntityTypeConfiguration<NotificationRecord>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("Locations");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.Property(l => l.Address).HasMaxLength(300).IsRequired();
        builder.Property(l => l.City).HasMaxLength(100).IsRequired();
        builder.Property(l => l.State).HasMaxLength(100);
        builder.Property(l => l.PostalCode).HasMaxLength(20);
        builder.Property(l => l.Country).HasMaxLength(50).IsRequired();
        builder.Property(l => l.TimeZoneId).HasMaxLength(100).IsRequired();

        builder.HasIndex(l => new { l.TenantId, l.Name });
    }

    public void Configure(EntityTypeBuilder<BusinessHour> builder)
    {
        builder.ToTable("BusinessHours");
        builder.HasKey(bh => bh.Id);

        builder.HasOne(bh => bh.Location)
            .WithMany(l => l.BusinessHours)
            .HasForeignKey(bh => bh.LocationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Staff> builder)
    {
        builder.ToTable("StaffMembers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.LastName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Email).HasMaxLength(256).IsRequired();
        builder.Property(s => s.ColorHex).HasMaxLength(10).IsRequired();

        builder.HasOne(s => s.Location)
            .WithMany()
            .HasForeignKey(s => s.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.TenantId, s.LocationId });
    }

    public void Configure(EntityTypeBuilder<StaffService> builder)
    {
        builder.ToTable("StaffServices");
        builder.HasKey(ss => new { ss.StaffId, ss.ServiceId });

        builder.Property(ss => ss.CustomPrice).HasPrecision(18, 2);

        builder.HasOne(ss => ss.Staff)
            .WithMany(s => s.StaffServices)
            .HasForeignKey(ss => ss.StaffId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ss => ss.Service)
            .WithMany(s => s.StaffServices)
            .HasForeignKey(ss => ss.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<ServiceCategory> builder)
    {
        builder.ToTable("ServiceCategories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.DisplayOrder });
    }

    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("Services");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Price).HasPrecision(18, 2);
        builder.Property(s => s.Currency).HasMaxLength(10).IsRequired();

        builder.HasOne(s => s.Category)
            .WithMany(c => c.Services)
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(s => new { s.TenantId, s.IsActive });
    }

    public void Configure(EntityTypeBuilder<WorkingHour> builder)
    {
        builder.ToTable("WorkingHours");
        builder.HasKey(w => w.Id);

        builder.HasOne(w => w.Staff)
            .WithMany(s => s.WorkingHours)
            .HasForeignKey(w => w.StaffId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(w => w.Location)
            .WithMany()
            .HasForeignKey(w => w.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(w => new { w.TenantId, w.StaffId, w.DayOfWeek });
    }

    public void Configure(EntityTypeBuilder<WorkingHourInterval> builder)
    {
        builder.ToTable("WorkingHourIntervals");
        builder.HasKey(i => i.Id);

        builder.HasOne(i => i.WorkingHour)
            .WithMany(w => w.Intervals)
            .HasForeignKey(i => i.WorkingHourId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("Holidays");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Name).HasMaxLength(150).IsRequired();

        builder.HasOne(h => h.Location)
            .WithMany()
            .HasForeignKey(h => h.LocationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The booking guard's holiday read ORs a date range with RecurringAnnually, so no seek on (TenantId, Date)
        // can answer it without LocationId and RecurringAnnually — and paying those as key lookups into a clustered
        // index keyed on a random Guid is what makes the read expensive. Measured on the guard's own statement:
        // 40,040 rows holding a 40-row tenant cost 124 logical reads uncovered and 4 covered; at 200,040 rows the
        // optimizer dropped the index altogether and read the whole table in one clustered scan, 3,045 reads for the
        // 10 rows it returned. See PERFORMANCE.md §7 and DATABASE.md §3.2, pinned by HolidayIndexTests.
        builder.HasIndex(h => new { h.TenantId, h.Date })
            .IncludeProperties(h => new { h.LocationId, h.RecurringAnnually })
            .HasDatabaseName("IX_Holidays_TenantId_Date");
    }

    public void Configure(EntityTypeBuilder<AvailabilityException> builder)
    {
        builder.ToTable("AvailabilityExceptions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reason).HasMaxLength(300);

        builder.HasOne(e => e.Staff)
            .WithMany(s => s.AvailabilityExceptions)
            .HasForeignKey(e => e.StaffId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.StaffId, e.StartDateTimeUtc, e.EndDateTimeUtc });
    }

    public void Configure(EntityTypeBuilder<ResourceGroup> builder)
    {
        builder.ToTable("ResourceGroups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(150).IsRequired();

        builder.HasIndex(g => new { g.TenantId, g.Name });
    }

    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("Resources");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(150).IsRequired();

        builder.HasOne(r => r.ResourceGroup)
            .WithMany(g => g.Resources)
            .HasForeignKey(r => r.ResourceGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Location)
            .WithMany()
            .HasForeignKey(r => r.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.ResourceGroupId, r.LocationId });
    }

    public void Configure(EntityTypeBuilder<ServiceResourceRequirement> builder)
    {
        builder.ToTable("ServiceResourceRequirements");
        builder.HasKey(rr => new { rr.ServiceId, rr.ResourceGroupId });

        builder.HasOne(rr => rr.Service)
            .WithMany(s => s.ResourceRequirements)
            .HasForeignKey(rr => rr.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rr => rr.ResourceGroup)
            .WithMany()
            .HasForeignKey(rr => rr.ResourceGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<AppointmentResource> builder)
    {
        builder.ToTable("AppointmentResources");
        builder.HasKey(ar => new { ar.AppointmentId, ar.ResourceId });

        builder.HasOne(ar => ar.Appointment)
            .WithMany(a => a.AppointmentResources)
            .HasForeignKey(ar => ar.AppointmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ar => ar.Resource)
            .WithMany()
            .HasForeignKey(ar => ar.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(c => c.LastName).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(256).IsRequired();
        builder.Property(c => c.TotalSpent).HasPrecision(18, 2);

        builder.HasIndex(c => new { c.TenantId, c.Email });
        builder.HasIndex(c => new { c.TenantId, c.LastName, c.FirstName });
    }

    public void Configure(EntityTypeBuilder<CustomerNote> builder)
    {
        builder.ToTable("CustomerNotes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.NoteText).HasMaxLength(2000).IsRequired();

        builder.HasOne(n => n.Customer)
            .WithMany(c => c.CustomerNotes)
            .HasForeignKey(n => n.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("Appointments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Price).HasPrecision(18, 2);
        builder.Property(a => a.Currency).HasMaxLength(10).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(1000);
        builder.Property(a => a.InternalNotes).HasMaxLength(1000);
        builder.Property(a => a.CancellationReason).HasMaxLength(500);
        builder.Property(a => a.IdempotencyKey).HasMaxLength(256);

        builder.Property(a => a.RowVersion)
            .IsRowVersion();

        builder.HasOne(a => a.Location)
            .WithMany()
            .HasForeignKey(a => a.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Service)
            .WithMany()
            .HasForeignKey(a => a.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Staff)
            .WithMany()
            .HasForeignKey(a => a.StaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Customer)
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.RecurringAppointment)
            .WithMany(r => r.Appointments)
            .HasForeignKey(a => a.RecurringAppointmentId)
            .OnDelete(DeleteBehavior.SetNull);

        // Crucial composite indexes for scheduling, availability, and reports
        builder.HasIndex(a => new { a.TenantId, a.StaffId, a.StartAtUtc, a.EndAtUtc })
            .HasDatabaseName("IX_Appointments_Tenant_Staff_TimeRange");

        builder.HasIndex(a => new { a.TenantId, a.CustomerId, a.StartAtUtc })
            .HasDatabaseName("IX_Appointments_Tenant_Customer_StartAt");

        // Occupancy read for one location over a forward window. A nonclustered index can seek only one range
        // column, so EndAtUtc leads: bounded by the tenant's booking horizon rather than by its accumulated
        // history, which StartAtUtc would drag in. The INCLUDE set is where the measured win came from
        // (363 -> 29 logical reads) — without it the optimizer keeps intersecting three indexes and key-lookup.
        builder.HasIndex(a => new { a.TenantId, a.LocationId, a.EndAtUtc })
            .IncludeProperties(a => new { a.StartAtUtc, a.StaffId, a.Status })
            .HasDatabaseName("IX_Appointments_Tenant_Location_EndAt");

        builder.HasIndex(a => new { a.TenantId, a.Status, a.StartAtUtc })
            .HasDatabaseName("IX_Appointments_Tenant_Status_StartAt");

        builder.HasIndex(a => new { a.TenantId, a.IdempotencyKey })
            .HasDatabaseName("IX_Appointments_Tenant_IdempotencyKey");
    }

    public void Configure(EntityTypeBuilder<AppointmentStatusHistory> builder)
    {
        builder.ToTable("AppointmentStatusHistories");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Reason).HasMaxLength(500);

        builder.HasOne(h => h.Appointment)
            .WithMany(a => a.StatusHistories)
            .HasForeignKey(h => h.AppointmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(h => h.AppointmentId);
    }

    public void Configure(EntityTypeBuilder<RecurringAppointment> builder)
    {
        builder.ToTable("RecurringAppointments");
        builder.HasKey(r => r.Id);

        builder.HasIndex(r => new { r.TenantId, r.StartDate });
    }

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.Currency).HasMaxLength(10).IsRequired();
        builder.Property(p => p.ProviderPaymentId).HasMaxLength(256);
        builder.Property(p => p.IdempotencyKey).HasMaxLength(256);

        builder.HasOne(p => p.Appointment)
            .WithMany()
            .HasForeignKey(p => p.AppointmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.TenantId, p.AppointmentId });
        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey });
    }

    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Amount).HasPrecision(18, 2);
        builder.Property(t => t.ProviderTransactionId).HasMaxLength(256);

        builder.HasOne(t => t.Payment)
            .WithMany(p => p.Transactions)
            .HasForeignKey(t => t.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.ProviderRefundId).HasMaxLength(256);

        builder.HasOne(r => r.Payment)
            .WithMany(p => p.Refunds)
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.ToTable("Reviews");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Comment).HasMaxLength(2000);
        builder.Property(r => r.Response).HasMaxLength(2000);

        builder.HasOne(r => r.Appointment)
            .WithMany()
            .HasForeignKey(r => r.AppointmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Staff)
            .WithMany()
            .HasForeignKey(r => r.StaffId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Service)
            .WithMany()
            .HasForeignKey(r => r.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Strict business rule: Only 1 review per appointment
        builder.HasIndex(r => new { r.TenantId, r.AppointmentId })
            .IsUnique()
            .HasDatabaseName("IX_Reviews_Tenant_Appointment_Unique");

        builder.HasIndex(r => new { r.TenantId, r.StaffId, r.Rating });
        builder.HasIndex(r => new { r.TenantId, r.ServiceId, r.Rating });
    }

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasMaxLength(250).IsRequired();
        builder.Property(m => m.Content).IsRequired();

        // Filtered index for active pending outbox jobs
        builder.HasIndex(m => new { m.ProcessedOnUtc, m.NextRetryTimeUtc })
            .HasFilter("[ProcessedOnUtc] IS NULL")
            .HasDatabaseName("IX_OutboxMessages_PendingQueue");
    }

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(150).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(150).IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(100);

        builder.HasIndex(a => new { a.TenantId, a.TimestampUtc });
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }

    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasMaxLength(256);
        builder.Property(r => r.RequestHash).HasMaxLength(256).IsRequired();

        builder.HasIndex(r => r.ExpiresAtUtc);
    }

    public void Configure(EntityTypeBuilder<NotificationRecord> builder)
    {
        builder.ToTable("NotificationRecords");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Recipient).HasMaxLength(256).IsRequired();
        builder.Property(n => n.Channel).HasMaxLength(50).IsRequired();
        builder.Property(n => n.Subject).HasMaxLength(300).IsRequired();

        builder.HasIndex(n => new { n.TenantId, n.IsSent });
    }
}
