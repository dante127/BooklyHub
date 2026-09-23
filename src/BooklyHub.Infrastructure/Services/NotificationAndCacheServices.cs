using System.Text.Json;
using BooklyHub.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.Services;

public class CacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<CacheService> _logger;

    public CacheService(IDistributedCache cache, ILogger<CacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _cache.GetStringAsync(key, cancellationToken);
            if (string.IsNullOrEmpty(data)) return default;
            return JsonSerializer.Deserialize<T>(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache key {Key}", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            var options = new DistributedCacheEntryOptions();
            if (expiration.HasValue)
            {
                options.SetAbsoluteExpiration(expiration.Value);
            }
            else
            {
                options.SetSlidingExpiration(TimeSpan.FromHours(1));
            }

            await _cache.SetStringAsync(key, json, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set cache key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache key {Key}", key);
        }
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // For distributed cache without server-level keys scanning, key prefixes can be invalidated or managed via versioned tags
        return Task.CompletedTask;
    }
}

public class SimulatedEmailSender : IEmailSender
{
    private readonly ILogger<SimulatedEmailSender> _logger;

    public SimulatedEmailSender(ILogger<SimulatedEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[EMAIL DISPATCHED] To: {To}, Subject: {Subject}", to, subject);
        return Task.CompletedTask;
    }
}

public class SimulatedSmsSender : ISmsSender
{
    private readonly ILogger<SimulatedSmsSender> _logger;

    public SimulatedSmsSender(ILogger<SimulatedSmsSender> logger)
    {
        _logger = logger;
    }

    public Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SMS DISPATCHED] To: {Phone}, Message: {Message}", phoneNumber, message);
        return Task.CompletedTask;
    }
}

public class SimulatedPushNotificationSender : IPushNotificationSender
{
    private readonly ILogger<SimulatedPushNotificationSender> _logger;

    public SimulatedPushNotificationSender(ILogger<SimulatedPushNotificationSender> logger)
    {
        _logger = logger;
    }

    public Task SendPushAsync(string recipientId, string title, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[PUSH DISPATCHED] Recipient: {Recipient}, Title: {Title}", recipientId, title);
        return Task.CompletedTask;
    }
}
