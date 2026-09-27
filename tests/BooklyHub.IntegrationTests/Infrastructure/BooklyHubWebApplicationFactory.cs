using System.Net.Http.Headers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Outbox;
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

    /// <summary>
    /// SQL Server used by this test host. CI injects ConnectionStrings__DefaultConnection for the
    /// service container; without an injected value the tests fall back to a localdb database.
    /// </summary>
    public string ConnectionString { get; }

    public QueryCountInterceptor QueryInterceptor { get; } = new();

    public BooklyHubWebApplicationFactory()
    {
        var injected =
            Environment.GetEnvironmentVariable("BOOKLYHUB_TEST_CONNECTIONSTRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

        // Each test class gets its own factory and xUnit runs classes in parallel, so every host
        // needs a private database: DisposeAsync deletes it, and a shared name would let one class
        // drop the schema another is still querying.
        var connectionString = string.IsNullOrWhiteSpace(injected)
            ? "Server=(localdb)\\mssqllocaldb;Database=placeholder;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
            : injected;

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = _dbName
        };

        ConnectionString = builder.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                ["ConnectionStrings:Redis"] = "", // Non-production hosts may use the in-memory cache
                ["Jwt:Secret"] = "BooklyHub_IntegrationTest_Only_SigningKey_NotForProduction_256bit!",
                ["AutoMigrateAndSeed"] = "false"
            };
            config.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureServices(services =>
        {
            // The polling workers issue their own SQL against the same database, which makes the
            // query-count assertions in the performance tests non-deterministic.
            var backgroundWorkers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && (d.ImplementationType == typeof(OutboxProcessorBackgroundService)
                                || d.ImplementationType == typeof(AppointmentReminderBackgroundService)))
                .ToList();

            foreach (var worker in backgroundWorkers)
            {
                services.Remove(worker);
            }

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
