using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BooklyHub.Api.Middlewares;
using BooklyHub.Application;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Data.Seeding;
using BooklyHub.Infrastructure.MultiTenancy;
using BooklyHub.Infrastructure.Security;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Add Layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Add Controllers with JSON Enum Converters
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddHttpContextAccessor();

// An error that never reaches an action — a 401 from the JWT challenge, a 403 from the permission
// handler, a 404 for a path nothing maps — used to be answered as a bare status code with no content
// type and no body, while every error thrown inside an action carried a problem document. Two shapes
// for one job. Registering the problem-details writer lets the status-code middleware emit the same
// envelope; instance and correlation id are filled in here because the writer leaves both out, and the
// title because the framework's status table has no entry for a rate-limited 429.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var problem = context.ProblemDetails;
        if (problem is null)
        {
            return;
        }

        if (problem.Title is null && problem.Status is { } status)
        {
            problem.Title = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
        }

        problem.Instance ??= context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : null;

        // A 403 the user reports is otherwise unfindable in the logs: the correlation id is the one
        // handle support has, and the exception path already carries it.
        if (context.HttpContext.Response.Headers.TryGetValue(CorrelationIdMiddleware.CorrelationIdHeader, out var correlationId))
        {
            problem.Extensions["correlationId"] = correlationId.ToString();
        }
    };
});

// Rate Limiting
const string AuthRateLimitPolicy = "auth";
const int AuthPermitsPerMinute = 10;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A request over the budget used to sit in a 10-deep queue until the window turned: four authenticated
    // GETs parked past five seconds without ever getting a status, and /health answered 429 because it was
    // behind the same queue. Refusing immediately says the same thing sooner and holds nothing open.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        // A liveness probe is not traffic, and it must not be measured in the same bucket as traffic: /health
        // answered 429 on a window an anonymous caller had just burned. It is not left unlimited either,
        // because the readiness check runs a database query, so an unthrottled probe is a cheap way to flood SQL.
        if (httpContext.Request.Path.StartsWithSegments("/health"))
        {
            return RateLimitPartition.GetFixedWindowLimiter("health", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        }

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    // docs/SECURITY.md 3.1 promised this tier and no code implemented it, so login spent the general 100/min
    // bucket like any other request (measured: 95 unmatched-route GETs left exactly five permits for the next
    // twenty login attempts). The partition is the address, not the account, because a limiter counts requests
    // and cannot know whether one failed. That is why the threshold here is not the account's defence: SEC-04(b)
    // counts failures on the user row and answers below this limit (`LoginLockoutPolicy`).
    options.AddPolicy(AuthRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{AuthRateLimitPolicy}:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = AuthPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Without this the client is told "too many" and nothing about when to come back: no rejection in the
    // measurement carried Retry-After, so a well-behaved caller has no way to back off instead of retrying now.
    options.OnRejected = (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        return ValueTask.CompletedTask;
    };
});

// Two deployment decisions the pipeline below installs or skips. They are made here because their options
// are service registrations, and the container is frozen by the time the pipeline is written. The reading
// itself — what counts as declared, and what gets the host refused — is ServingSurface, which the unit
// suite covers; what is left here is only the wiring, and the measured reason each middleware is optional.
var servingSurface = ServingSurface.FromConfiguration(builder.Configuration);

// A redirect needs a target this process can name. UseHttpsRedirection() with no port configured works it
// out per request from the server's own bindings, finds none in a container that binds http://+:8080 and
// holds no certificate, and writes "Failed to determine the https port for redirect" for every plain
// request it then serves anyway (measured). So it runs only when the operator names the port worth
// redirecting to, which is also the only port that is right behind a TLS-terminating proxy.
if (servingSurface.HttpsPort is { } httpsRedirectPort)
{
    builder.Services.Configure<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>(
        options => options.HttpsPort = httpsRedirectPort);
}

// The host's own host filter, narrowed to the names the operator gave. "*" or nothing leaves it admitting every
// Host, which is the state a deployment with no declaration is in.
if (servingSurface.AllowedHosts.Count > 0)
{
    builder.Services.Configure<HostFilteringOptions>(options =>
    {
        options.AllowedHosts = [.. servingSurface.AllowedHosts];
    });
}

// Health Checks. Only the database is a readiness condition: every request path reads it, so an instance
// that cannot reach it must be taken out of rotation. Nothing else is tagged, which is a measured choice
// rather than an omission — see docs/DEPLOYMENT.md for why the cache is not a probe target.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("Database", tags: new[] { BooklyHub.Api.Health.HealthEndpoints.ReadinessTag });

// Swagger / OpenAPI with JWT Security Definition
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "BooklyHub API",
        Version = "v1",
        Description = "Production-grade multi-tenant appointment and booking SaaS platform."
    });

    var scheme = new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    };

    c.AddSecurityDefinition("Bearer", scheme);

    c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer")] = new List<string>()
    });
});

var app = builder.Build();

// Database preparation is opt-in, never implied by the environment: an instance must not migrate
// or seed itself on boot unless an operator asked for it. `--migrate-only` performs the same step
// as a one-shot job and exits before the web server starts.
// Failures are deliberately unhandled so the process exits instead of serving traffic against an
// out-of-date or empty schema.
if (args.Contains("--migrate-only") || builder.Configuration.GetValue<bool>("AutoMigrateAndSeed"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var clock = scope.ServiceProvider.GetRequiredService<BooklyHub.Application.Common.Interfaces.IClock>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    await DatabaseSeeder.SeedAsync(db, hasher, clock, builder.Configuration, logger);

    if (args.Contains("--migrate-only"))
    {
        return;
    }
}

// Middleware Pipeline

// Everything below judges a caller by httpContext.Connection.RemoteIpAddress, and behind a TLS-terminating
// proxy that address is the proxy's: every visitor then shares the global 100/min and the auth 10/min buckets,
// so one anonymous caller locks the sign-in door for the whole deployment. The header the proxy writes carries
// the real caller, and reading it is only safe for a peer the operator has named — any caller can write this
// header, so an unnamed trust is an identity an attacker chooses. Nothing declared therefore means no header
// read, which leaves a host that has not described its topology with today's behaviour rather than a guess at
// it. First in the pipeline because the request log, the redirect and both limiters all read the address.
var forwarding = ForwardingSettings.FromConfiguration(builder.Configuration);
if (forwarding.IsConfigured)
{
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,

        // One hop, which is the topology this deployment family has. It is not the thing that stops a caller
        // rotating its identity with a longer chain: measured on this runtime, a two-entry chain resolves to its
        // rightmost entry whether the limit is set or not, so the prefix defence is the declared-peer check and
        // the facts below name it as such. The limit says the chain this host will read is one proxy deep.
        ForwardLimit = 1
    };

    // Measured: this constructor arrives with loopback already in its trust lists, so a host that names its
    // remote proxy still believes an X-Forwarded-For that arrives from the same machine — a sidecar, a second
    // container on the host network, any local process. Emptying both spellings of the list is what makes the
    // operator's declaration the whole declaration; the framework keeps an obsolete and a typed property for the
    // same idea and only measuring which one it reads would be a guess, so both go.
    // LoopbackProxyRateLimitTests is the fact that says this is load-bearing: with these three lines removed, a
    // request arriving from 127.0.0.1 through a host that declared 192.0.2.1 had its address rewritten.
    forwardedHeaders.KnownProxies.Clear();
#pragma warning disable ASPDEPR005 // The obsolete list is the one the runtime populated by default, so it is the
                                   // one that has to go; the typed property alone would leave the trust in place.
    forwardedHeaders.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
    forwardedHeaders.KnownIPNetworks.Clear();

    foreach (var proxy in forwarding.KnownProxies)
    {
        forwardedHeaders.KnownProxies.Add(proxy);
    }

    foreach (var network in forwarding.KnownNetworks)
    {
        // KnownIPNetworks, not KnownNetworks: the property that takes ASP.NET's own IPNetwork type is obsolete
        // on this runtime, and System.Net.IPNetwork is the one that will still be there.
        forwardedHeaders.KnownIPNetworks.Add(network);
    }

    // Measured on this runtime: a connection that carries no address gets the header applied whatever the trust
    // list says, because there is no peer to compare against and the framework reads that as permission rather
    // than as a refusal. A socket from Kestrel always has an address; a unix-socket listener does not, and a
    // deployment fronted over one would otherwise let any local process name its own client identity. No address
    // means no peer to check, and no peer to check means no trust, so the headers go before the middleware sees
    // them. AddresslessConnectionRateLimitTests is the fact that pins this.
    app.Use((context, next) =>
    {
        if (context.Connection.RemoteIpAddress is null)
        {
            context.Request.Headers.Remove("X-Forwarded-For");
            context.Request.Headers.Remove("X-Forwarded-Proto");
        }

        return next(context);
    });

    app.UseForwardedHeaders(forwardedHeaders);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Inside the exception handler (so a thrown exception stays its business) and outside authentication,
// authorization and endpoint dispatch (so a status code set without running an action still gets a body).
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "BooklyHub API v1");
    });
}

app.UseSerilogRequestLogging();

if (servingSurface.HttpsPort is not null)
{
    app.UseHttpsRedirection();
}

// Host filtering is not installed here. Measured on a build of this file with no host-filtering line at all,
// AllowedHosts=bookly.example answered 400 to Host: evil.example — minimal hosting puts this middleware in by
// itself and binds the same key by itself, so calling UseHostFiltering() here would only have added a second copy
// of it to every request. What the deployment was missing is the reading above: the host splits that key on ';'
// alone, so a comma list answers 400 to every Host including the two names it spells (measured), and an entry it
// cannot match fails silently at request time instead of at startup.

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// Health check endpoints
app.MapHealthChecks("/health", BooklyHub.Api.Health.HealthEndpoints.All);
app.MapHealthChecks("/health/live", BooklyHub.Api.Health.HealthEndpoints.Liveness);
app.MapHealthChecks("/health/ready", BooklyHub.Api.Health.HealthEndpoints.Readiness);

app.Run();

// Required for WebApplicationFactory in Integration Tests
public partial class Program { }
