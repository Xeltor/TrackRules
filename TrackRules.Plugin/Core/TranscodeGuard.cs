using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackRules.Core;

public sealed record TranscodeEvaluationContext(
    SessionInfo Session,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex);

public interface ITranscodeGuard
{
    Task<bool> ShouldSkipAsync(TranscodeEvaluationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Placeholder guard used until the PlaybackInfo probing endpoint is wired in.
/// </summary>
public sealed class TranscodeGuard : ITranscodeGuard
{
    private readonly ILogger<TranscodeGuard> _logger;

    public TranscodeGuard(ILogger<TranscodeGuard> logger)
    {
        _logger = logger;
    }

    public Task<bool> ShouldSkipAsync(TranscodeEvaluationContext context, CancellationToken cancellationToken)
    {
        // Future implementation: simulate PlaybackInfo to determine if the new stream combination
        // would drop from direct play/remux to transcode. For now we always allow.
        _logger.LogDebug("Transcode guard bypassed for session {Session}", context.Session.Id);
        return Task.FromResult(false);
    }
}
