using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BooklyHub.Infrastructure.Data;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BooklyHubDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True");

        var tenantContext = new TenantContext();
        var clock = new SystemClock();
        var currentUser = new DesignTimeCurrentUser();

        return new ApplicationDbContext(optionsBuilder.Options, tenantContext, currentUser, clock);
    }
}

internal class DesignTimeCurrentUser : ICurrentUser
{
    public Guid? UserId => Guid.Empty;
    public string? Email => "system@booklyhub.local";
    public IReadOnlyList<string> Roles => ["PlatformAdmin"];
    public IReadOnlyList<string> Permissions => [];
    public bool IsAuthenticated => true;
    public bool HasPermission(string permission) => true;
    public bool IsInRole(string role) => true;
}
