using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TrackRules.Core;

public interface ITrackRuleResolver
{
    ResolutionResult Resolve(UserRuleSet rules, ResolutionContext context);
}

/// <summary>
/// Input required to compute the desired stream indices.
/// </summary>
public sealed class ResolutionContext
{
    public ResolutionContext(
        Guid userId,
        Guid? seriesId,
        Guid? libraryId,
        IReadOnlyList<MediaStream> mediaStreams,
        int? currentAudioStreamIndex,
        int? currentSubtitleStreamIndex)
    {
        UserId = userId;
        SeriesId = seriesId;
        LibraryId = libraryId;
        MediaStreams = mediaStreams ?? Array.Empty<MediaStream>();
        CurrentAudioStreamIndex = currentAudioStreamIndex;
        CurrentSubtitleStreamIndex = currentSubtitleStreamIndex;
    }

    public Guid UserId { get; }

    public Guid? SeriesId { get; }

    public Guid? LibraryId { get; }

    public IReadOnlyList<MediaStream> MediaStreams { get; }

    public int? CurrentAudioStreamIndex { get; }

    public int? CurrentSubtitleStreamIndex { get; }
}

/// <summary>
/// Structured resolver output.
/// </summary>
public sealed class ResolutionResult
{
    public static readonly ResolutionResult NoChange = new(null, null, null, null);

    public ResolutionResult(
        TrackRule? appliedRule,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        RuleScope? scope)
    {
        AppliedRule = appliedRule;
        AudioStreamIndex = audioStreamIndex;
        SubtitleStreamIndex = subtitleStreamIndex;
        Scope = scope;
    }

    public TrackRule? AppliedRule { get; }

    public int? AudioStreamIndex { get; }

    public int? SubtitleStreamIndex { get; }

    public RuleScope? Scope { get; }

    public bool HasChanges => AudioStreamIndex.HasValue || SubtitleStreamIndex.HasValue;
}

/// <summary>
/// Deterministic resolver that enforces precedence and tie breakers.
/// </summary>
public sealed class TrackRuleResolver : ITrackRuleResolver
{
    private static readonly IReadOnlyList<string> _codecPreference =
    [
        "eac3",
        "truehd",
        "dts",
        "dtshd",
        "ac3",
        "aac",
        "flac",
        "opus",
        "vorbis",
        "pcm",
        "mp3"
    ];

    private readonly ILanguageNormalizer _normalizer;

    public TrackRuleResolver(ILanguageNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public ResolutionResult Resolve(UserRuleSet rules, ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (rules is null || context.MediaStreams.Count == 0)
        {
            return ResolutionResult.NoChange;
        }

        var enabledRules = rules.EnabledRules.ToList();
        if (enabledRules.Count == 0)
        {
            return ResolutionResult.NoChange;
        }

        var rule = SelectRule(enabledRules, context.SeriesId, context.LibraryId);
        if (rule is null)
        {
            return ResolutionResult.NoChange;
        }

        var normalizedAudioPrefs = NormalizeOrFallback(rule.Audio, RuleKeywords.Any);
        var normalizedSubPrefs = NormalizeOrFallback(rule.Subs, RuleKeywords.None);
        var audioStreams = context.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var subtitleStreams = context.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

        MediaStream? audioCandidate = TrySelectAudioStream(audioStreams, normalizedAudioPrefs);
        var selectedAudioLanguage = _normalizer.Normalize(audioCandidate?.Language);

        var subtitleDecision = TrySelectSubtitleStream(
            subtitleStreams,
            normalizedSubPrefs,
            rule.SubsMode,
            normalizedAudioPrefs,
            selectedAudioLanguage);

        var desiredAudioIndex = ComputeAudioChange(audioCandidate, context.CurrentAudioStreamIndex);
        var desiredSubtitleIndex = ComputeSubtitleChange(
            subtitleDecision,
            context.CurrentSubtitleStreamIndex);

        if (!desiredAudioIndex.HasValue && !desiredSubtitleIndex.HasValue)
        {
            return ResolutionResult.NoChange;
        }

        return new ResolutionResult(
            rule,
            desiredAudioIndex,
            desiredSubtitleIndex,
            rule.Scope);
    }

    private TrackRule? SelectRule(IEnumerable<TrackRule> rules, Guid? seriesId, Guid? libraryId)
    {
        var seriesRule = rules.FirstOrDefault(rule => rule.Scope == RuleScope.Series && rule.AppliesTo(seriesId));
        if (seriesRule is not null)
        {
            return seriesRule;
        }

        var libraryRule = rules.FirstOrDefault(rule => rule.Scope == RuleScope.Library && rule.AppliesTo(libraryId));
        if (libraryRule is not null)
        {
            return libraryRule;
        }

        return rules.FirstOrDefault(rule => rule.Scope == RuleScope.Global);
    }

    private IReadOnlyList<string> NormalizeOrFallback(IEnumerable<string> values, string fallback)
    {
        var list = values?.ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            list.Add(fallback);
        }

        var normalized = _normalizer.NormalizeMany(list);
        if (normalized.Count == 0)
        {
            return new[] { fallback };
        }

        return normalized;
    }

    private MediaStream? TrySelectAudioStream(IReadOnlyList<MediaStream> audioStreams, IReadOnlyList<string> preferences)
    {
        if (audioStreams.Count == 0)
        {
            return null;
        }

        foreach (var preference in preferences)
        {
            MediaStream? candidate;
            if (preference.Equals(RuleKeywords.Any, StringComparison.OrdinalIgnoreCase))
            {
                candidate = audioStreams
                    .OrderByDescending(ScoreAudioStream)
                    .FirstOrDefault();
            }
            else
            {
                candidate = audioStreams
                    .Where(stream => LanguageMatches(stream.Language, preference))
                    .OrderByDescending(ScoreAudioStream)
                    .FirstOrDefault();
            }

            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private SubtitleDecision TrySelectSubtitleStream(
        IReadOnlyList<MediaStream> subtitleStreams,
        IReadOnlyList<string> preferences,
        SubtitleMode mode,
        IReadOnlyList<string> audioPreferences,
        string? selectedAudioLanguage)
    {
        if (mode == SubtitleMode.None ||
            (preferences.Count == 1 && preferences[0].Equals(RuleKeywords.None, StringComparison.OrdinalIgnoreCase)))
        {
            return SubtitleDecision.Disable();
        }

        if (subtitleStreams.Count == 0)
        {
            return SubtitleDecision.NoChange();
        }

        if (mode == SubtitleMode.OnlyIfAudioNotPreferred &&
            AudioMatchesPreference(selectedAudioLanguage, audioPreferences))
        {
            return SubtitleDecision.Disable();
        }

        return mode switch
        {
            SubtitleMode.PreferForced => SelectPreferForced(subtitleStreams, preferences),
            SubtitleMode.Always => SelectAlways(subtitleStreams, preferences),
            SubtitleMode.OnlyIfAudioNotPreferred => SelectDefault(subtitleStreams, preferences),
            _ => SelectDefault(subtitleStreams, preferences)
        };
    }

    private SubtitleDecision SelectPreferForced(IReadOnlyList<MediaStream> subtitles, IReadOnlyList<string> preferences)
    {
        var forced = FindByPreference(subtitles, preferences, stream => stream.IsForced);
        if (forced is not null)
        {
            return SubtitleDecision.Use(forced);
        }

        var fallbackForced = subtitles.FirstOrDefault(stream => stream.IsForced);
        if (fallbackForced is not null)
        {
            return SubtitleDecision.Use(fallbackForced);
        }

        return SelectDefault(subtitles, preferences);
    }

    private SubtitleDecision SelectAlways(IReadOnlyList<MediaStream> subtitles, IReadOnlyList<string> preferences)
    {
        var firstPreference = preferences.FirstOrDefault(pref => !pref.Equals(RuleKeywords.None, StringComparison.OrdinalIgnoreCase));
        if (firstPreference is null)
        {
            var firstAvailable = subtitles.OrderByDescending(ScoreSubtitleStream).FirstOrDefault();
            return firstAvailable is null ? SubtitleDecision.NoChange() : SubtitleDecision.Use(firstAvailable);
        }

        var match = FindByPreference(subtitles, preferences, _ => true);
        if (match is not null)
        {
            return SubtitleDecision.Use(match);
        }

        var fallback = subtitles.OrderByDescending(ScoreSubtitleStream).FirstOrDefault();
        return fallback is null ? SubtitleDecision.NoChange() : SubtitleDecision.Use(fallback);
    }

    private SubtitleDecision SelectDefault(IReadOnlyList<MediaStream> subtitles, IReadOnlyList<string> preferences)
    {
        var defaultMatch = FindByPreference(subtitles.Where(s => s.IsDefault).ToList(), preferences, _ => true);
        if (defaultMatch is not null)
        {
            return SubtitleDecision.Use(defaultMatch);
        }

        var fallbackDefault = subtitles.FirstOrDefault(stream => stream.IsDefault);
        if (fallbackDefault is not null)
        {
            return SubtitleDecision.Use(fallbackDefault);
        }

        var preferred = FindByPreference(subtitles, preferences, _ => true);
        if (preferred is not null)
        {
            return SubtitleDecision.Use(preferred);
        }

        return SubtitleDecision.NoChange();
    }

    private MediaStream? FindByPreference(
        IReadOnlyList<MediaStream> candidates,
        IReadOnlyList<string> preferences,
        Func<MediaStream, bool> predicate)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var preference in preferences)
        {
            if (preference.Equals(RuleKeywords.None, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (preference.Equals(RuleKeywords.Any, StringComparison.OrdinalIgnoreCase))
            {
                var anyMatch = candidates
                    .Where(predicate)
                    .OrderByDescending(ScoreSubtitleStream)
                    .FirstOrDefault();

                if (anyMatch is not null)
                {
                    return anyMatch;
                }

                continue;
            }

            var match = candidates
                .Where(stream => predicate(stream) && LanguageMatches(stream.Language, preference))
                .OrderByDescending(ScoreSubtitleStream)
                .FirstOrDefault();

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private bool AudioMatchesPreference(string? selectedAudioLanguage, IReadOnlyList<string> audioPreferences)
    {
        if (string.IsNullOrEmpty(selectedAudioLanguage))
        {
            return false;
        }

        return audioPreferences.Any(preference =>
            !preference.Equals(RuleKeywords.Any, StringComparison.OrdinalIgnoreCase) &&
            preference.Equals(selectedAudioLanguage, StringComparison.OrdinalIgnoreCase));
    }

    private bool LanguageMatches(string? streamLanguage, string preference)
    {
        var normalized = _normalizer.Normalize(streamLanguage);
        return normalized.Equals(preference, StringComparison.OrdinalIgnoreCase);
    }

    private int? ComputeAudioChange(MediaStream? candidate, int? currentIndex)
    {
        if (candidate is null)
        {
            return null;
        }

        return currentIndex == candidate.Index ? null : candidate.Index;
    }

    private int? ComputeSubtitleChange(SubtitleDecision decision, int? currentIndex)
    {
        var current = currentIndex ?? -1;

        if (decision.DisableTracks)
        {
            return current < 0 ? null : -1;
        }

        if (decision.Stream is null)
        {
            return null;
        }

        var desired = decision.Stream.Index;
        return current == desired ? null : desired;
    }

    private static int ScoreAudioStream(MediaStream stream)
    {
        var channelScore = stream.Channels ?? 0;
        var codec = (stream.Codec ?? string.Empty).ToLowerInvariant();
        var codecScore = _codecPreference.Count - IndexOf(codec, _codecPreference);
        var defaultScore = stream.IsDefault ? 1000 : 0;

        return defaultScore + (channelScore * 10) + codecScore;
    }

    private static int ScoreSubtitleStream(MediaStream stream)
    {
        return (stream.IsDefault ? 10 : 0) + (stream.IsForced ? 5 : 0);
    }

    private static int IndexOf(string value, IReadOnlyList<string> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return list.Count;
    }

    private sealed record SubtitleDecision(bool DisableTracks, MediaStream? Stream)
    {
        public static SubtitleDecision Disable() => new(true, null);

        public static SubtitleDecision NoChange() => new(false, null);

        public static SubtitleDecision Use(MediaStream stream) => new(false, stream);
    }
}
