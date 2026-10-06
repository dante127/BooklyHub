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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Swapped in for the host's <see cref="BooklyHub.Application.Common.Interfaces.IClock"/> so a test can pass
    /// time rather than wait for it. It is the application's clock only: the distributed cache and the JWT
    /// stack keep their own real-time timers, which is precisely the disagreement an expiring key has to
    /// survive.
    /// </summary>
    public TestClock Clock { get; } = new();

    /// <summary>
    /// What the host logged while this fixture lived. SEC-09: the two claims that finding is about — a dispatch
    /// that carries no customer address, a refusal that names which of its four causes it was — are claims about
    /// the log, and a response body cannot answer either of them.
    /// </summary>
    public CollectingLogger Logs { get; } = new();

    /// <summary>
    /// Last-mile service overrides (recording email senders and the like), applied after the test host has
    /// replaced the connection string and removed the polling workers. A virtual hook rather than a
    /// constructor argument because xUnit class fixtures must be constructible with no parameters.
    /// </summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>
    /// Configuration a derived fixture adds for its own facts, merged into the same in-memory source that
    /// carries the test connection string and signing key. Needed because some host behaviour is decided by
    /// configuration before the pipeline is built — <c>Forwarding:KnownProxies</c> registers a middleware or
    /// does not — and a service override cannot reach a decision the pipeline already made.
    /// </summary>
    protected virtual void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
    }

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

            ConfigureTestConfiguration(testConfig);

            config.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureServices(services =>
        {
            // The polling workers issue their own SQL against the same database, which makes the
            // query-count assertions in the performance tests non-deterministic — and the no-show sweep
            // would close rows a test is still about to read. The sweep tests call it themselves. The retention
            // sweep is on this list because it deletes: leaving it running was measured taking a refresh-token
            // test down with "Execution Timeout Expired" after 60s of contending with the test's own writes, and
            // which test lost varied between runs. A worker that empties tables a test is reading back is not a
            // flaky test, it is a destructive job that was never asked to run.
            var backgroundWorkers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && (d.ImplementationType == typeof(OutboxProcessorBackgroundService)
                                || d.ImplementationType == typeof(AppointmentReminderBackgroundService)
                                || d.ImplementationType == typeof(AppointmentNoShowBackgroundService)
                                || d.ImplementationType == typeof(RetentionSweepBackgroundService)))
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

            services.Replace(ServiceDescriptor.Singleton<BooklyHub.Application.Common.Interfaces.IClock>(Clock));

            // Registered last so it is the ILogger<> the container hands out, and registered as an open generic
            // rather than as an ILoggerProvider because the provider is not a seam this host writes through:
            // UseSerilog() replaces ILoggerFactory with Serilog's own, which never enumerates the registered
            // providers. See CollectingLogger for the measurement.
            services.AddSingleton(Logs);
            services.AddSingleton(typeof(ILogger<>), typeof(CollectingLogger<>));

            ConfigureTestServices(services);
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
