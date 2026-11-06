namespace ExamPrism.Infrastructure.MultiTenancy;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class RedisXmlRepository : IXmlRepository
{
    private static readonly ConcurrentDictionary<string, List<string>> FallbackCache = new(StringComparer.Ordinal);
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisKey _key;
    private readonly ILogger<RedisXmlRepository>? _logger;

    public RedisXmlRepository(
        IConnectionMultiplexer multiplexer,
        string key,
        ILogger<RedisXmlRepository>? logger = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        try
        {
            var db = _multiplexer.GetDatabase();
            var values = db.ListRange(_key);
            if (values is not null && values.Length > 0)
            {
                var elements = new List<XElement>(values.Length);
                var cache = FallbackCache.GetOrAdd((string)_key!, static _ => new List<string>());
                lock (cache)
                {
                    foreach (var val in values)
                    {
                        if (val.HasValue)
                        {
                            var xmlStr = val.ToString();
                            elements.Add(XElement.Parse(xmlStr));
                            if (!cache.Contains(xmlStr))
                            {
                                cache.Add(xmlStr);
                            }
                        }
                    }
                }

                return elements.AsReadOnly();
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger?.LogWarning(ex, "Failed to retrieve DataProtection XML elements from Redis at key {Key}", (string)_key!);
        }

        if (FallbackCache.TryGetValue((string)_key!, out var fallbackList))
        {
            lock (fallbackList)
            {
                if (fallbackList.Count > 0)
                {
                    return fallbackList.Select(x => XElement.Parse(x)).ToList().AsReadOnly();
                }
            }
        }

        return Array.Empty<XElement>();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        var xmlString = element.ToString(SaveOptions.DisableFormatting);
        var cache = FallbackCache.GetOrAdd((string)_key!, static _ => new List<string>());
        lock (cache)
        {
            if (!cache.Contains(xmlString))
            {
                cache.Add(xmlString);
            }
        }

        try
        {
            var db = _multiplexer.GetDatabase();
            db.ListRightPush(_key, xmlString);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger?.LogWarning(ex, "Failed to store DataProtection XML element ({FriendlyName}) to Redis at key {Key}", friendlyName, (string)_key!);
        }
    }
}
