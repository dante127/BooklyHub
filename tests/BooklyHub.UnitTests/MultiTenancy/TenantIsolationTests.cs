using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Exceptions;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace BooklyHub.UnitTests.MultiTenancy;

public class TenantIsolationTests
{
    private readonly IClock _clock = new SystemClock();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private ApplicationDbContext CreateDbContext(ITenantContext tenantContext, string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new ApplicationDbContext(options, tenantContext, _currentUser, _clock);
    }

    [Fact]
    public async Task QueryFilter_ShouldStrictlyIsolateDataBetweenTenants()
    {
        var dbName = Guid.NewGuid().ToString();

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        // 1. Seed data using a platform admin context
        var adminContext = new TenantContext();
        adminContext.SetTenant(Guid.Empty, isPlatformAdmin: true);

        using (var seedContext = CreateDbContext(adminContext, dbName))
        {
            seedContext.Customers.AddRange(
                new Customer { Id = Guid.NewGuid(), TenantId = tenantAId, FirstName = "Alice", LastName = "Smith", Email = "alice@a.com" },
                new Customer { Id = Guid.NewGuid(), TenantId = tenantAId, FirstName = "Adam", LastName = "Jones", Email = "adam@a.com" },
                new Customer { Id = Guid.NewGuid(), TenantId = tenantBId, FirstName = "Bob", LastName = "Brown", Email = "bob@b.com" }
            );

            seedContext.Services.AddRange(
                new Service { Id = Guid.NewGuid(), TenantId = tenantAId, Name = "Teeth Cleaning", Price = 100 },
                new Service { Id = Guid.NewGuid(), TenantId = tenantBId, Name = "Haircut", Price = 45 }
            );

            await seedContext.SaveChangesAsync();
        }

        // 2. Query as Tenant A
        var tenantAContext = new TenantContext();
        tenantAContext.SetTenant(tenantAId, isPlatformAdmin: false);

        using (var contextA = CreateDbContext(tenantAContext, dbName))
        {
            var customers = await contextA.Customers.ToListAsync();
            customers.Should().HaveCount(2);
            customers.Should().AllSatisfy(c => c.TenantId.Should().Be(tenantAId));
            customers.Should().NotContain(c => c.FirstName == "Bob");

            var services = await contextA.Services.ToListAsync();
            services.Should().HaveCount(1);
            services.First().Name.Should().Be("Teeth Cleaning");
        }

        // 3. Query as Tenant B
        var tenantBContext = new TenantContext();
        tenantBContext.SetTenant(tenantBId, isPlatformAdmin: false);

        using (var contextB = CreateDbContext(tenantBContext, dbName))
        {
            var customers = await contextB.Customers.ToListAsync();
            customers.Should().HaveCount(1);
            customers.First().FirstName.Should().Be("Bob");

            var services = await contextB.Services.ToListAsync();
            services.Should().HaveCount(1);
            services.First().Name.Should().Be("Haircut");
        }

        // 4. Query as unauthenticated / empty tenant
        var emptyContext = new TenantContext();

        using (var contextEmpty = CreateDbContext(emptyContext, dbName))
        {
            var customers = await contextEmpty.Customers.ToListAsync();
            customers.Should().BeEmpty();

            var services = await contextEmpty.Services.ToListAsync();
            services.Should().BeEmpty();
        }

        // 5. Query as Platform Admin
        using (var contextAdmin = CreateDbContext(adminContext, dbName))
        {
            var customers = await contextAdmin.Customers.ToListAsync();
            customers.Should().HaveCount(3);

            var services = await contextAdmin.Services.ToListAsync();
            services.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task SaveChanges_TenantPrincipalWritingForeignTenantEntity_MustBeRefused()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantAId, isPlatformAdmin: false);

        using var db = CreateDbContext(tenantContext, Guid.NewGuid().ToString());

        db.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantBId,
            FirstName = "Mallory",
            LastName = "Smith",
            Email = "mallory@b.com"
        });

        var foreignWrite = async () => await db.SaveChangesAsync();

        await foreignWrite.Should().ThrowAsync<CrossTenantAccessViolationException>();

        db.ChangeTracker.Clear();

        // The same principal keeps full rights over its own tenant's rows.
        db.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantAId,
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@a.com"
        });

        await db.SaveChangesAsync();
        (await db.Customers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SaveChanges_PlatformAdminAndUnboundWrites_MustStayPossible()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var adminContext = new TenantContext();
        adminContext.SetTenant(Guid.Empty, isPlatformAdmin: true);

        using (var db = CreateDbContext(adminContext, dbName))
        {
            db.Customers.AddRange(
                new Customer { Id = Guid.NewGuid(), TenantId = tenantAId, FirstName = "A", LastName = "A", Email = "a@a.com" },
                new Customer { Id = Guid.NewGuid(), TenantId = tenantBId, FirstName = "B", LastName = "B", Email = "b@b.com" });

            await db.SaveChangesAsync();
        }

        // Background workers run with no tenant bound, so they still write across tenants.
        using (var db = CreateDbContext(new TenantContext(), dbName))
        {
            var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();

            foreach (var customer in customers)
            {
                db.CustomerNotes.Add(new CustomerNote
                {
                    TenantId = customer.TenantId,
                    CustomerId = customer.Id,
                    NoteText = "Written by a system worker"
                });
            }

            await db.SaveChangesAsync();
        }

        using (var verify = CreateDbContext(adminContext, dbName))
        {
            (await verify.CustomerNotes.CountAsync()).Should().Be(2);
        }
    }
}
