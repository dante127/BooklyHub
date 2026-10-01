using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BooklyHub.Api.Middlewares;
using BooklyHub.Application;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Data.Seeding;
using BooklyHub.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 10
        });
    });
});

// Health Checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("Database");

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
app.UseHttpsRedirection();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// Health check endpoints
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => true });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") || true });

app.Run();

// Required for WebApplicationFactory in Integration Tests
public partial class Program { }
