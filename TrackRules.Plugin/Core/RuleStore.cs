using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackRules.Core;

public interface IRuleStore
{
    Task<UserRuleSet> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveAsync(UserRuleSet rules, CancellationToken cancellationToken);
}

/// <summary>
/// Persists per-user rule sets as JSON documents under the server data directory.
/// </summary>
public sealed class RuleStore : IRuleStore
{
    private readonly ILogger<RuleStore> _logger;
    private readonly string _userStorePath;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RuleStore(IApplicationPaths applicationPaths, ILogger<RuleStore> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _logger = logger;
        _userStorePath = Path.Combine(applicationPaths.DataPath, "TrackRules");
        Directory.CreateDirectory(_userStorePath);
    }

    public async Task<UserRuleSet> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var semaphore = GetLock(userId);
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = GetUserFile(userId);

            if (!File.Exists(path))
            {
                return UserRuleSet.Create(userId);
            }

            await using var stream = File.OpenRead(path);
            var payload = await JsonSerializer.DeserializeAsync<UserRuleSet>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                _logger.LogWarning("Rules for user {UserId} could not be deserialized, recreating defaults.", userId);
                return UserRuleSet.Create(userId);
            }

            if (payload.Version != RuleSchema.CurrentVersion)
            {
                payload.Version = RuleSchema.CurrentVersion;
            }

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load rules for user {UserId}", userId);
            return UserRuleSet.Create(userId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task SaveAsync(UserRuleSet rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var semaphore = GetLock(rules.UserId);
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = GetUserFile(rules.UserId);
            rules.Version = RuleSchema.CurrentVersion;

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, rules, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist rules for user {UserId}", rules.UserId);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private string GetUserFile(Guid userId)
    {
        return Path.Combine(_userStorePath, $"{userId:N}.json");
    }

    private SemaphoreSlim GetLock(Guid key)
    {
        return _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }
}
