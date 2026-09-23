using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Security;
using BooklyHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BooklyHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
                               ?? "Server=(localdb)\\mssqllocaldb;Database=BooklyHubDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
            });
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        // Contexts & Services
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IClock, SystemClock>();

        // Distributed Cache (Redis or fallback MemoryCache)
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "BooklyHub:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddSingleton<ICacheService, CacheService>();
        services.AddScoped<BooklyHub.Application.Scheduling.IAvailabilityService, AvailabilityService>();
        services.AddScoped<BooklyHub.Application.Payments.IPaymentProvider, BooklyHub.Infrastructure.Payments.SimulatedPaymentProvider>();
        services.AddScoped<BooklyHub.Application.Payments.IIdempotencyService, BooklyHub.Infrastructure.Payments.IdempotencyService>();

        // Security & Authentication
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();

        var jwtSecret = configuration["Jwt:Secret"] ?? "BooklyHub_SuperSecret_Jwt_SigningKey_For_Production_Saas_2026!";
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "BooklyHub";
        var jwtAudience = configuration["Jwt:Audience"] ?? "BooklyHubClients";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret)),
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });

        services.AddAuthorization();

        // Notification senders (Default simulated implementations)
        services.AddScoped<IEmailSender, SimulatedEmailSender>();
        services.AddScoped<ISmsSender, SimulatedSmsSender>();
        services.AddScoped<IPushNotificationSender, SimulatedPushNotificationSender>();

        // Background workers
        services.AddHostedService<BooklyHub.Infrastructure.Outbox.OutboxProcessorBackgroundService>();
        services.AddHostedService<BooklyHub.Infrastructure.BackgroundJobs.AppointmentReminderBackgroundService>();

        return services;
    }
}
