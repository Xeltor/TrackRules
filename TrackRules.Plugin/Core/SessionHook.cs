using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackRules.Core;

/// <summary>
/// Hooks into playback start events to enforce rule selections.
/// </summary>
public sealed class SessionHook : IHostedService, IDisposable
{
    private readonly ILogger<SessionHook> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IRuleStore _ruleStore;
    private readonly ITrackRuleResolver _resolver;
    private readonly ITranscodeGuard _transcodeGuard;
    private bool _disposed;

    public SessionHook(
        ILogger<SessionHook> logger,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IRuleStore ruleStore,
        ITrackRuleResolver resolver,
        ITranscodeGuard transcodeGuard)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _ruleStore = ruleStore;
        _resolver = resolver;
        _transcodeGuard = transcodeGuard;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _disposed = true;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        _ = Task.Run(() => HandlePlaybackStartAsync(e), CancellationToken.None);
    }

    private async Task HandlePlaybackStartAsync(PlaybackProgressEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.Session is null || eventArgs.Session.UserId == Guid.Empty)
            {
                _logger.LogDebug("PlaybackStart skipped because the session is missing.");
                return;
            }

            var session = eventArgs.Session;
            var mediaStreams = eventArgs.MediaInfo?.MediaStreams ?? Array.Empty<MediaStream>();
            if (mediaStreams.Length == 0)
            {
                _logger.LogDebug("Session {SessionId} has no media streams to analyze.", session.Id);
                return;
            }

            var userId = session.UserId;
            var ruleSet = await _ruleStore.GetAsync(userId, CancellationToken.None).ConfigureAwait(false);
            if (ruleSet.Rules.Count == 0)
            {
                _logger.LogDebug("User {UserId} has no Track Rules.", userId);
                return;
            }

            var seriesId = eventArgs.MediaInfo?.SeriesId;
            var libraryId = ResolveLibraryId(eventArgs.Item);

            var streamSet = Array.AsReadOnly(mediaStreams);

            var context = new ResolutionContext(
                userId,
                seriesId,
                libraryId,
                streamSet,
                session.PlayState?.AudioStreamIndex,
                session.PlayState?.SubtitleStreamIndex);

            var resolution = _resolver.Resolve(ruleSet, context);
            if (!resolution.HasChanges)
            {
                return;
            }

            if (resolution.AppliedRule?.DontTranscode == true)
            {
                var guardContext = new TranscodeEvaluationContext(
                    session,
                    resolution.AudioStreamIndex,
                    resolution.SubtitleStreamIndex);

                var shouldSkip = await _transcodeGuard.ShouldSkipAsync(guardContext, CancellationToken.None).ConfigureAwait(false);
                if (shouldSkip)
                {
                    _logger.LogInformation(
                        "Skipping track change for session {SessionId} to honor dontTranscode rule.",
                        session.Id);
                    return;
                }
            }

            if (resolution.AudioStreamIndex.HasValue)
            {
                await SendGeneralCommandAsync(
                    session,
                    GeneralCommandType.SetAudioStreamIndex,
                    resolution.AudioStreamIndex.Value).ConfigureAwait(false);
            }

            if (resolution.SubtitleStreamIndex.HasValue)
            {
                await SendGeneralCommandAsync(
                    session,
                    GeneralCommandType.SetSubtitleStreamIndex,
                    resolution.SubtitleStreamIndex.Value).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Applied Track Rules ({Scope}) to session {SessionId}: audio={Audio}, subs={Subs}",
                resolution.Scope,
                session.Id,
                resolution.AudioStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "-",
                resolution.SubtitleStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce Track Rules on playback start.");
        }
    }

    private Guid? ResolveLibraryId(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var folder = _libraryManager
            .GetCollectionFolders(item)
            .FirstOrDefault();

        return folder?.Id;
    }

    private Task SendGeneralCommandAsync(SessionInfo session, GeneralCommandType commandType, int index)
    {
        var command = new GeneralCommand
        {
            Name = commandType,
            ControllingUserId = session.UserId
        };
        command.Arguments["Index"] = index.ToString(CultureInfo.InvariantCulture);

        return _sessionManager.SendGeneralCommand(
            session.Id,
            session.Id,
            command,
            CancellationToken.None);
    }
}
