using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TrackRules.Core;

/// <summary>
/// Constants that describe the persisted rule schema.
/// </summary>
public static class RuleSchema
{
    /// <summary>
    /// Current schema version.
    /// </summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// Scopes used when matching rules.
/// </summary>
public enum RuleScope
{
    Global = 0,
    Library = 1,
    Series = 2
}

/// <summary>
/// Subtitle selection modes supported by the resolver.
/// </summary>
public enum SubtitleMode
{
    None = 0,
    Default = 1,
    PreferForced = 2,
    Always = 3,
    OnlyIfAudioNotPreferred = 4
}

/// <summary>
/// A single rule entry persisted per user.
/// </summary>
public sealed class TrackRule
{
    public RuleScope Scope { get; set; }

    public Guid? TargetId { get; set; }

    public List<string> Audio { get; set; } = new() { RuleKeywords.Any };

    public List<string> Subs { get; set; } = new() { RuleKeywords.None };

    public SubtitleMode SubsMode { get; set; } = SubtitleMode.Default;

    public bool DontTranscode { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AppliesTo(Guid? candidate)
    {
        if (Scope == RuleScope.Global)
        {
            return true;
        }

        if (!TargetId.HasValue || !candidate.HasValue)
        {
            return false;
        }

        return TargetId.Value.Equals(candidate.Value);
    }
}

/// <summary>
/// Fixed keywords understood by the resolver.
/// </summary>
public static class RuleKeywords
{
    public const string Any = "any";
    public const string None = "none";
}

/// <summary>
/// Aggregate rules per user.
/// </summary>
public sealed class UserRuleSet
{
    public int Version { get; set; } = RuleSchema.CurrentVersion;

    public Guid UserId { get; set; }

    public List<TrackRule> Rules { get; set; } = new();

    public IEnumerable<TrackRule> EnabledRules => Rules.Where(rule => rule.Enabled);

    public static UserRuleSet Create(Guid userId)
    {
        return new UserRuleSet
        {
            UserId = userId,
            Version = RuleSchema.CurrentVersion,
            Rules = new List<TrackRule>()
        };
    }
}
