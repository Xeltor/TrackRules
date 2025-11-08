using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.TrackRules.Core;

namespace Jellyfin.Plugin.TrackRules.Api;

/// <summary>
/// Public-facing representation of all rules for a user.
/// </summary>
public sealed class UserRulesDto
{
    public int Version { get; set; } = RuleSchema.CurrentVersion;

    [Required]
    public Guid UserId { get; set; }

    public List<TrackRuleDto> Rules { get; set; } = new();
}

/// <summary>
/// DTO describing an individual rule entry.
/// </summary>
public sealed class TrackRuleDto
{
    public RuleScopeDto Scope { get; set; }

    public Guid? TargetId { get; set; }

    public List<string> Audio { get; set; } = new();

    public List<string> Subs { get; set; } = new();

    public SubtitleModeDto SubsMode { get; set; } = SubtitleModeDto.Default;

    public bool DontTranscode { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// DTO describing a preview request payload.
/// </summary>
public sealed class PreviewRequestDto
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public Guid ItemId { get; set; }
}

/// <summary>
/// DTO returned by the preview endpoint.
/// </summary>
public sealed class PreviewResultDto
{
    public RuleScopeDto? Scope { get; set; }

    public int? AudioStreamIndex { get; set; }

    public int? SubtitleStreamIndex { get; set; }

    public string? Reason { get; set; }

    public bool TranscodeRisk { get; set; }
}

/// <summary>
/// DTO describing an apply request payload.
/// </summary>
public sealed class ApplyRequestDto
{
    [Required]
    public string? SessionId { get; set; }

    public int? AudioStreamIndex { get; set; }

    public int? SubtitleStreamIndex { get; set; }
}

/// <summary>
/// Public scope enum so API is decoupled from the internal model.
/// </summary>
public enum RuleScopeDto
{
    Global = 0,
    Library = 1,
    Series = 2
}

/// <summary>
/// Public subtitle mode enum so API is decoupled from the internal model.
/// </summary>
public enum SubtitleModeDto
{
    None = 0,
    Default = 1,
    PreferForced = 2,
    Always = 3,
    OnlyIfAudioNotPreferred = 4
}

/// <summary>
/// Utility helpers used to convert model objects to public DTOs and back.
/// </summary>
internal static class TrackRuleDtoMapper
{
    public static UserRulesDto ToDto(UserRuleSet domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        return new UserRulesDto
        {
            Version = domain.Version,
            UserId = domain.UserId,
            Rules = domain.Rules.Select(ToDto).ToList()
        };
    }

    public static UserRuleSet ToDomain(UserRulesDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var ruleSet = new UserRuleSet
        {
            UserId = dto.UserId,
            Version = dto.Version,
            Rules = dto.Rules.Select(ToDomain).ToList()
        };

        return ruleSet;
    }

    private static TrackRuleDto ToDto(TrackRule rule)
    {
        return new TrackRuleDto
        {
            Scope = (RuleScopeDto)rule.Scope,
            TargetId = rule.TargetId,
            Audio = rule.Audio?.ToList() ?? new List<string>(),
            Subs = rule.Subs?.ToList() ?? new List<string>(),
            SubsMode = (SubtitleModeDto)rule.SubsMode,
            DontTranscode = rule.DontTranscode,
            Enabled = rule.Enabled
        };
    }

    private static TrackRule ToDomain(TrackRuleDto dto)
    {
        return new TrackRule
        {
            Scope = (RuleScope)dto.Scope,
            TargetId = dto.TargetId,
            Audio = NormalizeList(dto.Audio, RuleKeywords.Any),
            Subs = NormalizeList(dto.Subs, RuleKeywords.None),
            SubsMode = (SubtitleMode)dto.SubsMode,
            DontTranscode = dto.DontTranscode,
            Enabled = dto.Enabled
        };
    }

    private static List<string> NormalizeList(List<string>? values, string fallback)
    {
        if (values is null || values.Count == 0)
        {
            return new List<string> { fallback };
        }

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
    }
}
