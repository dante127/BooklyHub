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
        // SEC-09: this line carried the customer's address and the subject of their appointment mail at
        // Information, which is the level every environment runs at, into whatever sink the console points at. A
        // dispatch still has to leave a trace — for these senders the trace is the only evidence one was asked
        // for — so the fact and the size of the payload stay and the recipient goes. Nothing is demoted to Debug
        // instead: there is no LogDebug anywhere in src and no host that runs at that level, so demotion would
        // keep the address in the file while letting the code claim it had been redacted.
        _logger.LogInformation("[EMAIL DISPATCHED] subject {SubjectLength} char(s), body {BodyLength} char(s)", subject.Length, htmlBody.Length);
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
        // SEC-09: the number was the address of a phone and the message was "your appointment is confirmed for
        // <when>" — a person's schedule, written next to their number, in a log an operator forwards to a vendor.
        _logger.LogInformation("[SMS DISPATCHED] message {MessageLength} char(s)", message.Length);
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
        // SEC-09: the recipient stays, because it is this server's own id and an id is what every other line in
        // this build carries (`AuthController` logs the same kind of value for a capped session). The title does
        // not: it is built from the patient's name and the service they booked.
        _logger.LogInformation("[PUSH DISPATCHED] recipient {Recipient}, title {TitleLength} char(s)", recipientId, title.Length);
        return Task.CompletedTask;
    }
}
