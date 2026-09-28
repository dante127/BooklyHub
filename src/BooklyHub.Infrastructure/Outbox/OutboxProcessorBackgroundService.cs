using System.Text.Json;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Events;
using BooklyHub.Infrastructure.MultiTenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.Outbox;

public class OutboxProcessorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessorBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public OutboxProcessorBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxProcessorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxProcessorBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing outbox processing loop.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("OutboxProcessorBackgroundService stopped.");
    }

    public async Task<int> ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        // The batch is claimed before it is worked: the lock lives for the whole transaction, so a second
        // instance polling the same pending rows can only skip this tick, never publish the same message.
        return await db.ExecuteInTransactionAsync(async () =>
        {
            if (!await db.TryAcquireOutboxProcessorLockAsync(cancellationToken))
            {
                return 0;
            }

            // Fetch batch of unprocessed messages that are due
            var messages = await db.OutboxMessages
                .Where(m => m.ProcessedOnUtc == null && (m.NextRetryTimeUtc == null || m.NextRetryTimeUtc <= now))
                .OrderBy(m => m.OccurredOnUtc)
                .Take(20)
                .ToListAsync(cancellationToken);

            if (messages.Count == 0) return 0;

            foreach (var message in messages)
            {
                try
                {
                    var eventType = ResolveEventType(message.Type);
                    if (eventType != null)
                    {
                        var domainEvent = JsonSerializer.Deserialize(message.Content, eventType);
                        INotification? notification = domainEvent switch
                        {
                            AppointmentCreatedEvent e => new BooklyHub.Application.Notifications.AppointmentCreatedNotification(e),
                            AppointmentCancelledEvent e => new BooklyHub.Application.Notifications.AppointmentCancelledNotification(e),
                            AppointmentRescheduledEvent e => new BooklyHub.Application.Notifications.AppointmentRescheduledNotification(e),
                            _ => null
                        };

                        if (notification != null)
                        {
                            await publisher.Publish(notification, cancellationToken);
                        }
                    }

                    message.ProcessedOnUtc = clock.UtcNow;
                    message.Error = null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process outbox message {MessageId} of type {Type}", message.Id, message.Type);

                    message.RetryCount++;
                    message.Error = ex.Message;

                    if (message.RetryCount >= 5)
                    {
                        // Mark as permanently failed after 5 retries to avoid poisoning the queue
                        message.ProcessedOnUtc = clock.UtcNow;
                        _logger.LogCritical("Outbox message {MessageId} permanently failed after 5 retries.", message.Id);
                    }
                    else
                    {
                        // Exponential backoff: 10s, 20s, 40s, 80s
                        var backoffSeconds = Math.Pow(2, message.RetryCount) * 5;
                        message.NextRetryTimeUtc = now.AddSeconds(backoffSeconds);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            return messages.Count;
        }, cancellationToken);
    }

    private static Type? ResolveEventType(string typeName)
    {
        return typeName switch
        {
            nameof(AppointmentCreatedEvent) => typeof(AppointmentCreatedEvent),
            nameof(AppointmentConfirmedEvent) => typeof(AppointmentConfirmedEvent),
            nameof(AppointmentCancelledEvent) => typeof(AppointmentCancelledEvent),
            nameof(AppointmentRescheduledEvent) => typeof(AppointmentRescheduledEvent),
            nameof(AppointmentCompletedEvent) => typeof(AppointmentCompletedEvent),
            _ => null
        };
    }
}
