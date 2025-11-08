using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.TrackRules.Core;

public interface ILanguageNormalizer
{
    string Normalize(string? value);

    IReadOnlyList<string> NormalizeMany(IEnumerable<string> values);
}

/// <summary>
/// Coerces user provided language codes into ISO639-3 form.
/// </summary>
public sealed class LanguageNormalizer : ILanguageNormalizer
{
    private static readonly ImmutableDictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "eng",
        ["eng"] = "eng",
        ["en"] = "eng",
        ["enus"] = "eng",
        ["jp"] = "jpn",
        ["jpn"] = "jpn",
        ["japanese"] = "jpn",
        ["ja"] = "jpn",
        ["fr"] = "fra",
        ["fra"] = "fra",
        ["fre"] = "fra",
        ["french"] = "fra",
        ["es"] = "spa",
        ["spa"] = "spa",
        ["spanish"] = "spa",
        ["latin spanish"] = "spa",
        ["castilian"] = "spa",
        ["de"] = "deu",
        ["ger"] = "deu",
        ["deu"] = "deu",
        ["german"] = "deu",
        ["it"] = "ita",
        ["ita"] = "ita",
        ["italian"] = "ita",
        ["ko"] = "kor",
        ["kor"] = "kor",
        ["korean"] = "kor",
        ["zh"] = "zho",
        ["zho"] = "zho",
        ["chi"] = "zho",
        ["chinese"] = "zho",
        ["pt"] = "por",
        ["por"] = "por",
        ["portuguese"] = "por",
        ["br"] = "por",
        ["pb"] = "por",
        ["ptbr"] = "por",
        ["ru"] = "rus",
        ["rus"] = "rus",
        ["russian"] = "rus",
        ["pl"] = "pol",
        ["pol"] = "pol",
        ["polish"] = "pol",
        ["sv"] = "swe",
        ["swe"] = "swe",
        ["swedish"] = "swe",
        ["none"] = RuleKeywords.None,
        ["any"] = RuleKeywords.Any,
        ["und"] = "und",
        ["mul"] = "mul"
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (_aliases.TryGetValue(trimmed, out var mapped))
        {
            return mapped;
        }

        var normalized = trimmed.ToLowerInvariant();
        if (_aliases.TryGetValue(normalized, out mapped))
        {
            return mapped;
        }

        if (normalized.Length == 2 && TryConvertIso639Alpha2To3(normalized, out mapped))
        {
            return mapped;
        }

        return normalized;
    }

    public IReadOnlyList<string> NormalizeMany(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Select(Normalize)
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryConvertIso639Alpha2To3(string alpha2, out string converted)
    {
        converted = alpha2 switch
        {
            "en" => "eng",
            "ja" => "jpn",
            "fr" => "fra",
            "es" => "spa",
            "de" => "deu",
            "it" => "ita",
            "ko" => "kor",
            "zh" => "zho",
            "pt" => "por",
            "ru" => "rus",
            "pl" => "pol",
            "sv" => "swe",
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(converted);
    }
}
