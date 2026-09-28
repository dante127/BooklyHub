using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.Reviews;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Entities.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BooklyHub.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<TenantSetting> TenantSettings { get; }
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Location> Locations { get; }
    DbSet<BusinessHour> BusinessHours { get; }
    DbSet<Staff> StaffMembers { get; }
    DbSet<StaffService> StaffServices { get; }
    DbSet<ServiceCategory> ServiceCategories { get; }
    DbSet<Service> Services { get; }
    DbSet<WorkingHour> WorkingHours { get; }
    DbSet<WorkingHourInterval> WorkingHourIntervals { get; }
    DbSet<Holiday> Holidays { get; }
    DbSet<AvailabilityException> AvailabilityExceptions { get; }
    DbSet<ResourceGroup> ResourceGroups { get; }
    DbSet<Resource> Resources { get; }
    DbSet<ServiceResourceRequirement> ServiceResourceRequirements { get; }
    DbSet<AppointmentResource> AppointmentResources { get; }
    DbSet<Customer> Customers { get; }
    DbSet<CustomerNote> CustomerNotes { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<AppointmentStatusHistory> AppointmentStatusHistories { get; }
    DbSet<RecurringAppointment> RecurringAppointments { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentTransaction> PaymentTransactions { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<Review> Reviews { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<NotificationRecord> NotificationRecords { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(IDbContextTransaction transaction, CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(IDbContextTransaction transaction, CancellationToken cancellationToken = default);
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken = default);
    /// <summary>
    /// Serializes every booking write inside one location. Resource contention is location-wide, so a
    /// staff-scoped lock alone lets two staff members take the same room simultaneously.
    /// Always acquired before <see cref="AcquireStaffLockAsync"/>; never the other way round.
    /// </summary>
    Task AcquireLocationBookingLockAsync(Guid tenantId, Guid locationId, CancellationToken cancellationToken = default);

    Task AcquireStaffLockAsync(Guid staffId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes every money write for one appointment. The booking locks protect a calendar; charging is
    /// contested per appointment and a lost race is a financial loss rather than a reschedulable conflict,
    /// so it gets its own key instead of widening the booking locks.
    /// </summary>
    Task AcquireAppointmentPaymentLockAsync(Guid tenantId, Guid appointmentId, CancellationToken cancellationToken = default);
}
