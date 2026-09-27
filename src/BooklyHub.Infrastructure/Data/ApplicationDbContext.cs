using System.Linq.Expressions;
using System.Text.Json;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Appointments;using BooklyHub.Domain.Entities.Customers;
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
using BooklyHub.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BooklyHub.Infrastructure.Data;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        IClock clock) : base(options)
    {
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _clock = clock;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<BusinessHour> BusinessHours => Set<BusinessHour>();
    public DbSet<Staff> StaffMembers => Set<Staff>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<ServiceCategory> ServiceCategories => Set<ServiceCategory>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<WorkingHour> WorkingHours => Set<WorkingHour>();
    public DbSet<WorkingHourInterval> WorkingHourIntervals => Set<WorkingHourInterval>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<AvailabilityException> AvailabilityExceptions => Set<AvailabilityException>();
    public DbSet<ResourceGroup> ResourceGroups => Set<ResourceGroup>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ServiceResourceRequirement> ServiceResourceRequirements => Set<ServiceResourceRequirement>();
    public DbSet<AppointmentResource> AppointmentResources => Set<AppointmentResource>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentStatusHistory> AppointmentStatusHistories => Set<AppointmentStatusHistory>();
    public DbSet<RecurringAppointment> RecurringAppointments => Set<RecurringAppointment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<NotificationRecord> NotificationRecords => Set<NotificationRecord>();

    public Guid? CurrentTenantId => _tenantContext.TenantId;
    public bool IsPlatformAdmin => _tenantContext.IsPlatformAdmin;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Apply Global Query Filters for ITenantEntity and ISoftDeletable across all entity types
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var isTenantEntity = typeof(ITenantEntity).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);

            if (isTenantEntity || isSoftDeletable)
            {
                var parameter = Expression.Parameter(clrType, "e");
                Expression? combinedFilter = null;

                if (isTenantEntity)
                {
                    // Filter: this.IsPlatformAdmin || ((Guid?)e.TenantId == this.CurrentTenantId)
                    var tenantIdProp = Expression.Property(parameter, nameof(ITenantEntity.TenantId));

                    var dbContextExpr = Expression.Constant(this);
                    var isPlatformAdminProp = Expression.Property(dbContextExpr, nameof(ApplicationDbContext.IsPlatformAdmin));
                    var currentTenantIdProp = Expression.Property(dbContextExpr, nameof(ApplicationDbContext.CurrentTenantId));

                    var nullableTenantId = Expression.Convert(tenantIdProp, typeof(Guid?));
                    var tenantMatch = Expression.Equal(nullableTenantId, currentTenantIdProp);
                    var tenantFilter = Expression.OrElse(isPlatformAdminProp, tenantMatch);

                    combinedFilter = tenantFilter;
                }

                if (isSoftDeletable)
                {
                    // Filter: !e.IsDeleted
                    var isDeletedProp = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                    var notDeleted = Expression.Not(isDeletedProp);

                    combinedFilter = combinedFilter == null
                        ? notDeleted
                        : Expression.AndAlso(combinedFilter, notDeleted);
                }

                if (combinedFilter != null)
                {
                    var lambda = Expression.Lambda(combinedFilter, parameter);
                    modelBuilder.Entity(clrType).HasQueryFilter(lambda);
                }
            }
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var currentUserId = _currentUser.UserId?.ToString() ?? "System";

        // Handle Auditable, Soft Delete, and Tenant Assignment
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ITenantEntity tenantEntity)
            {
                if (tenantEntity.TenantId == Guid.Empty && entry.State == EntityState.Added && _tenantContext.TenantId.HasValue)
                {
                    tenantEntity.TenantId = _tenantContext.TenantId.Value;
                }

                // Read queries are already scoped by the global filter; this closes the write path, so a
                // tenant principal cannot persist an entity stamped with another tenant even if a handler
                // or a future code path forgets to validate the identifier it was handed.
                if (!_tenantContext.IsPlatformAdmin &&
                    _tenantContext.TenantId is Guid currentTenantId &&
                    tenantEntity.TenantId != currentTenantId &&
                    entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    throw new CrossTenantAccessViolationException(
                        $"Cannot write {entry.Metadata.ClrType.Name} belonging to tenant {tenantEntity.TenantId}.");
                }
            }

            if (entry.Entity is IAuditableEntity auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAtUtc = now;
                    auditable.CreatedBy = currentUserId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.LastModifiedAtUtc = now;
                    auditable.LastModifiedBy = currentUserId;
                }
            }

            if (entry.Entity is ISoftDeletable softDeletable && entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                softDeletable.IsDeleted = true;
                softDeletable.DeletedAtUtc = now;
                softDeletable.DeletedBy = currentUserId;
            }
        }

        // Process Domain Events -> Outbox Messages
        var domainEntities = ChangeTracker.Entries<Entity<Guid>>()
            .Where(x => x.Entity.DomainEvents.Any())
            .ToList();

        var outboxEntries = new List<OutboxMessage>();
        foreach (var entity in domainEntities)
        {
            foreach (var domainEvent in entity.Entity.DomainEvents)
            {
                var eventType = domainEvent.GetType().Name;
                var eventJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType());

                outboxEntries.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    OccurredOnUtc = now,
                    Type = eventType,
                    Content = eventJson
                });
            }

            entity.Entity.ClearDomainEvents();
        }

        if (outboxEntries.Count > 0)
        {
            OutboxMessages.AddRange(outboxEntries);
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        return await Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(IDbContextTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RollbackTransactionAsync(IDbContextTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await transaction.RollbackAsync(cancellationToken);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            var result = await operation();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    public async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Serializes every booking write inside one location. Resource groups are shared by all staff at a
    /// location, so a staff-scoped lock leaves the last room free for two concurrent bookings by two
    /// different staff members. Take this lock before AcquireStaffLockAsync, never after.
    /// </summary>
    public Task AcquireLocationBookingLockAsync(Guid tenantId, Guid locationId, CancellationToken cancellationToken = default)
        => ExecuteAppLockAsync($"Booking_Location_{tenantId:N}_{locationId:N}", cancellationToken);

    public Task AcquireStaffLockAsync(Guid staffId, Guid tenantId, CancellationToken cancellationToken = default)
        => ExecuteAppLockAsync($"Booking_Staff_{tenantId:N}_{staffId:N}", cancellationToken);

    private async Task ExecuteAppLockAsync(string lockKey, CancellationToken cancellationToken)
    {
        if (!Database.IsSqlServer()) return;

        await Database.ExecuteSqlRawAsync(
            "DECLARE @res INT; EXEC @res = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; IF @res < 0 THROW 50000, 'Booking lock acquisition failed', 1;",
            [lockKey],
            cancellationToken);
    }
}
