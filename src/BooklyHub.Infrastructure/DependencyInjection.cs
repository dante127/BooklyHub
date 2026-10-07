using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Security;
using BooklyHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BooklyHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:DefaultConnection' is missing or empty. There is no built-in fallback connection string.");
        }

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

        // Distributed cache. The in-process fallback is per-instance, so cached state is not
        // shared between nodes and is lost on restart.
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "BooklyHub:";
            });
        }
        else if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:Redis' is required in Production. The in-memory fallback is single-node only.");
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        // Refuses the stand-in bindings below wherever their result would be believed as real: Production
        // may not run the payment simulator or the log-only senders. The selection that passes there is
        // 'None', and it binds providers that refuse, so the guard decides what a Production host can say and
        // these lines decide what it means.
        OutboundProviderPolicy.Validate(configuration, environment);

        var simulatedPayments = OutboundProviderPolicy.IsSelected(
            configuration, OutboundProviderPolicy.PaymentsKey, OutboundProviderPolicy.Simulated);
        var simulatedNotifications = OutboundProviderPolicy.IsSelected(
            configuration, OutboundProviderPolicy.NotificationsKey, OutboundProviderPolicy.Simulated);

        services.AddSingleton<ICacheService, CacheService>();
        services.AddScoped<BooklyHub.Application.Scheduling.IAvailabilityService, AvailabilityService>();
        services.AddScoped(typeof(BooklyHub.Application.Payments.IPaymentProvider), simulatedPayments
            ? typeof(BooklyHub.Infrastructure.Payments.SimulatedPaymentProvider)
            : typeof(BooklyHub.Infrastructure.Outbound.NonePaymentProvider));
        services.AddScoped<BooklyHub.Application.Payments.IIdempotencyService, BooklyHub.Infrastructure.Payments.IdempotencyService>();

        // Security & Authentication
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IRefreshTokenProtector, RefreshTokenProtector>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Eager check so a missing or weak signing key aborts startup instead of failing the first request.
        JwtSigningSettings.FromConfiguration(configuration);

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            // Resolved inside the options callback, not at registration time: configuration sources
            // added later must be visible so validation uses the same key the token generator reads.
            var jwtSettings = JwtSigningSettings.FromConfiguration(configuration);

            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSettings.Secret)),
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });

        services.AddAuthorization();

        // Notification senders. The simulated ones log instead of delivering, which OutboundProviderPolicy has
        // already refused for Production; the None ones throw, which is the only way a sender whose signature
        // has no failure value can keep a delivery from being recorded.
        services.AddScoped(typeof(IEmailSender), simulatedNotifications
            ? typeof(BooklyHub.Infrastructure.Services.SimulatedEmailSender)
            : typeof(BooklyHub.Infrastructure.Outbound.NoneEmailSender));
        services.AddScoped(typeof(ISmsSender), simulatedNotifications
            ? typeof(BooklyHub.Infrastructure.Services.SimulatedSmsSender)
            : typeof(BooklyHub.Infrastructure.Outbound.NoneSmsSender));
        services.AddScoped(typeof(IPushNotificationSender), simulatedNotifications
            ? typeof(BooklyHub.Infrastructure.Services.SimulatedPushNotificationSender)
            : typeof(BooklyHub.Infrastructure.Outbound.NonePushNotificationSender));

        // Background workers
        services.AddHostedService<BooklyHub.Infrastructure.Outbox.OutboxProcessorBackgroundService>();
        services.AddHostedService<BooklyHub.Infrastructure.BackgroundJobs.AppointmentReminderBackgroundService>();
        services.AddHostedService<BooklyHub.Infrastructure.BackgroundJobs.AppointmentNoShowBackgroundService>();

        // DB-04: the append-only operational tables have no other writer that removes rows, so without this the
        // four horizons in RetentionPolicy are a wish list.
        services.AddHostedService<BooklyHub.Infrastructure.BackgroundJobs.RetentionSweepBackgroundService>();

        return services;
    }
}
