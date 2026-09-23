using System.Net.Http.Headers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BooklyHub.IntegrationTests.Infrastructure;

public class BooklyHubWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"BooklyHub_Test_{Guid.NewGuid():N}";
    public string ConnectionString => $"Server=(localdb)\\mssqllocaldb;Database={_dbName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

    public QueryCountInterceptor QueryInterceptor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                ["ConnectionStrings:Redis"] = "", // Fallback to memory cache in integration tests
                ["AutoMigrateAndSeed"] = "false"
            };
            config.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureServices(services =>
        {
            // Remove existing DbContext registration
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(ConnectionString, sql =>
                {
                    sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                });
                options.AddInterceptors(QueryInterceptor);
            });
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Ignore cleanup failure in localdb
        }

        await base.DisposeAsync();
    }

    public HttpClient CreateClientForTenant(Guid tenantId, string role, IReadOnlyList<string>? permissions = null, Guid? userId = null)
    {
        var client = CreateClient();

        using var scope = Services.CreateScope();
        var tokenGen = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var user = new User
        {
            Id = userId ?? Guid.NewGuid(),
            Email = $"test-{role.ToLower()}@test.com",
            FirstName = "Test",
            LastName = "User",
            TenantId = tenantId
        };

        var perms = permissions ?? Permissions.GetDefaultPermissionsForRole(role);
        var token = tokenGen.GenerateAccessToken(user, [role], perms);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());

        return client;
    }

    public HttpClient CreatePlatformAdminClient()
    {
        var client = CreateClient();

        using var scope = Services.CreateScope();
        var tokenGen = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "platform-admin@test.com",
            FirstName = "Platform",
            LastName = "Admin",
            TenantId = null
        };

        var token = tokenGen.GenerateAccessToken(user, [Roles.PlatformAdmin], Permissions.All);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
