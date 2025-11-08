using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrackRules.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackRules.Api;

/// <summary>
/// REST surface for manipulating Track Rules data and testing selections.
/// </summary>
[ApiController]
[Authorize]
[Route("TrackRules")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class TrackRulesController : ControllerBase
{
    private const string UserIdClaimType = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";

    private readonly IRuleStore _ruleStore;
    private readonly ITrackRuleResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILanguageNormalizer _languageNormalizer;
    private readonly ILogger<TrackRulesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRulesController"/> class.
    /// </summary>
    public TrackRulesController(
        IRuleStore ruleStore,
        ITrackRuleResolver resolver,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ISessionManager sessionManager,
        ILanguageNormalizer languageNormalizer,
        ILogger<TrackRulesController> logger)
    {
        _ruleStore = ruleStore;
        _resolver = resolver;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _sessionManager = sessionManager;
        _languageNormalizer = languageNormalizer;
        _logger = logger;
    }

    /// <summary>
    /// Returns the rules configured for the requested user.
    /// </summary>
    [HttpGet("user/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRulesDto>> GetUserRules(
        [FromRoute] Guid userId,
        CancellationToken cancellationToken)
    {
        if (!CanAccessUser(userId))
        {
            return Forbid();
        }

        var rules = await _ruleStore.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(TrackRuleDtoMapper.ToDto(rules));
    }

    /// <summary>
    /// Upserts the Track Rules for a user.
    /// </summary>
    [HttpPut("user/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRulesDto>> UpsertUserRules(
        [FromRoute] Guid userId,
        [FromBody] UserRulesDto payload,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!CanAccessUser(userId))
        {
            return Forbid();
        }

        if (payload is null)
        {
            return BadRequest("Payload is required.");
        }

        if (payload.UserId != Guid.Empty && payload.UserId != userId)
        {
            return BadRequest("UserId mismatch between route and payload.");
        }

        payload.UserId = userId;
        payload.Rules ??= new List<TrackRuleDto>();

        var domainRules = TrackRuleDtoMapper.ToDomain(payload);
        await _ruleStore.SaveAsync(domainRules, cancellationToken).ConfigureAwait(false);

        return Ok(TrackRuleDtoMapper.ToDto(domainRules));
    }

    /// <summary>
    /// Preview the rule resolution outcome for a given item.
    /// </summary>
    [HttpPost("preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreviewResultDto>> PreviewAsync(
        [FromBody] PreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (request is null)
        {
            return BadRequest("Request payload is required.");
        }

        if (!CanAccessUser(request.UserId))
        {
            return Forbid();
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound($"Item {request.ItemId} was not found.");
        }

        var mediaStreams = _mediaSourceManager.GetMediaStreams(request.ItemId);
        if (mediaStreams.Count == 0)
        {
            return Ok(new PreviewResultDto
            {
                Reason = "Item has no media streams."
            });
        }

        var ruleSet = await _ruleStore.GetAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        if (request.OverrideRule is not null)
        {
            var overrideRule = TrackRuleDtoMapper.ToDomainRule(request.OverrideRule);
            ApplyOverrideRule(ruleSet, overrideRule);
        }
        if (ruleSet.Rules.Count == 0)
        {
            return Ok(new PreviewResultDto
            {
                Reason = "User has no Track Rules configured."
            });
        }

        var context = new ResolutionContext(
            request.UserId,
            ResolveSeriesId(item),
            ResolveLibraryId(item),
            mediaStreams,
            currentAudioStreamIndex: null,
            currentSubtitleStreamIndex: null);

        var resolution = _resolver.Resolve(ruleSet, context);
        if (!resolution.HasChanges)
        {
            return Ok(new PreviewResultDto
            {
                Reason = "No matching rule for this item."
            });
        }

        var preview = new PreviewResultDto
        {
            Scope = resolution.Scope.HasValue ? (RuleScopeDto)resolution.Scope.Value : null,
            AudioStreamIndex = resolution.AudioStreamIndex,
            SubtitleStreamIndex = resolution.SubtitleStreamIndex,
            Reason = DescribeScope(resolution.Scope),
            TranscodeRisk = false
        };

        return Ok(preview);
    }

    /// <summary>
    /// Aggregates available languages for a series to populate the UI widget.
    /// </summary>
    [HttpGet("series/{seriesId:guid}/languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SeriesLanguageOptionsDto> GetSeriesLanguages([FromRoute] Guid seriesId)
    {
        var series = _libraryManager.GetItemById(seriesId);
        if (series is null)
        {
            return NotFound();
        }

        var options = AggregateSeriesLanguages(seriesId);
        return Ok(options);
    }

    /// <summary>
    /// Applies the supplied indices to an active session immediately.
    /// </summary>
    [HttpPost("apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ApplyAsync(
        [FromBody] ApplyRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (request is null)
        {
            return BadRequest("Request payload is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("SessionId is required.");
        }

        if (!request.AudioStreamIndex.HasValue && !request.SubtitleStreamIndex.HasValue)
        {
            return BadRequest("Provide at least one of audioStreamIndex or subtitleStreamIndex.");
        }

        var session = FindSession(request.SessionId);
        if (session is null)
        {
            return NotFound("Session not found.");
        }

        if (!CanAccessUser(session.UserId))
        {
            return Forbid();
        }

        if (request.AudioStreamIndex.HasValue)
        {
            await SendGeneralCommandAsync(
                session,
                GeneralCommandType.SetAudioStreamIndex,
                request.AudioStreamIndex.Value,
                cancellationToken).ConfigureAwait(false);
        }

        if (request.SubtitleStreamIndex.HasValue)
        {
            await SendGeneralCommandAsync(
                session,
                GeneralCommandType.SetSubtitleStreamIndex,
                request.SubtitleStreamIndex.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return Ok(new
        {
            sessionId = session.Id,
            request.AudioStreamIndex,
            request.SubtitleStreamIndex
        });
    }

    private SessionInfo? FindSession(string sessionId)
    {
        return _sessionManager
            .Sessions
            .FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyOverrideRule(UserRuleSet ruleSet, TrackRule overrideRule)
    {
        if (overrideRule is null)
        {
            return;
        }

        if (overrideRule.Scope != RuleScope.Global && overrideRule.TargetId is null)
        {
            return;
        }

        ruleSet.Rules.RemoveAll(rule =>
            rule.Scope == overrideRule.Scope &&
            Nullable.Equals(rule.TargetId, overrideRule.TargetId));

        ruleSet.Rules.Add(overrideRule);
    }

    private SeriesLanguageOptionsDto AggregateSeriesLanguages(Guid seriesId)
    {
        var aggregate = new LanguageAggregate();
        Guid? previewItemId = null;

        var query = new InternalItemsQuery
        {
            ParentId = seriesId,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode }
        };

        var items = _libraryManager.GetItemList(query);

        foreach (var item in items)
        {
            var streams = _mediaSourceManager.GetMediaStreams(item.Id);
            if (streams.Count == 0)
            {
                continue;
            }

            previewItemId ??= item.Id;

            foreach (var audioStream in streams.Where(stream => stream.Type == MediaStreamType.Audio))
            {
                var code = NormalizeLanguage(audioStream.Language);
                var label = ResolveLanguageLabel(code, audioStream.Title ?? audioStream.Language);
                aggregate.AddAudio(code, label);
            }

            foreach (var subtitleStream in streams.Where(stream => stream.Type == MediaStreamType.Subtitle))
            {
                var code = NormalizeLanguage(subtitleStream.Language);
                var label = ResolveLanguageLabel(code, subtitleStream.Title ?? subtitleStream.Language);
                aggregate.AddSubtitle(code, label);
            }
        }

        return new SeriesLanguageOptionsDto
        {
            SeriesId = seriesId,
            PreviewItemId = previewItemId,
            Audio = aggregate.GetAudioOptions(),
            Subtitles = aggregate.GetSubtitleOptions()
        };
    }

    private string NormalizeLanguage(string? language)
    {
        var normalized = _languageNormalizer.Normalize(language);
        return string.IsNullOrEmpty(normalized) ? "und" : normalized;
    }

    private static string ResolveLanguageLabel(string code, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown / Unspecified";
        }

        try
        {
            var culture = CultureInfo
                .GetCultures(CultureTypes.AllCultures)
                .FirstOrDefault(c =>
                    string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));

            if (culture is not null)
            {
                return culture.NativeName;
            }
        }
        catch (CultureNotFoundException)
        {
            // Ignore and fall back to the code.
        }

        return code.ToUpperInvariant();
    }

    private static Guid? ResolveSeriesId(BaseItem item)
    {
        if (item is IHasSeries hasSeries)
        {
            var seriesId = hasSeries.FindSeriesId();
            return seriesId == Guid.Empty ? null : seriesId;
        }

        return null;
    }

    private Guid? ResolveLibraryId(BaseItem item)
    {
        var folder = _libraryManager
            .GetCollectionFolders(item)
            .FirstOrDefault();

        return folder?.Id;
    }

    private bool CanAccessUser(Guid userId)
    {
        var authenticatedUserId = GetAuthenticatedUserId();

        if (authenticatedUserId == Guid.Empty)
        {
            return true;
        }

        if (userId == authenticatedUserId)
        {
            return true;
        }

        return User?.IsInRole(AdministratorRole) == true;
    }

    private Guid GetAuthenticatedUserId()
    {
        var value = User?.Claims?.FirstOrDefault(
            claim => claim.Type.Equals(UserIdClaimType, StringComparison.OrdinalIgnoreCase))?.Value;

        return Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
    }

    private sealed class LanguageAggregate
    {
        private readonly Dictionary<string, LanguageBucket> _audio = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LanguageBucket> _subtitles = new(StringComparer.OrdinalIgnoreCase);

        public void AddAudio(string code, string label)
        {
            Add(_audio, code, label);
        }

        public void AddSubtitle(string code, string label)
        {
            Add(_subtitles, code, label);
        }

        public List<LanguageOptionDto> GetAudioOptions()
        {
            return Project(_audio);
        }

        public List<LanguageOptionDto> GetSubtitleOptions()
        {
            return Project(_subtitles);
        }

        private static void Add(Dictionary<string, LanguageBucket> map, string code, string label)
        {
            var key = string.IsNullOrWhiteSpace(code) ? "und" : code;
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new LanguageBucket(key, label);
                map[key] = bucket;
            }

            bucket.Increment();
        }

        private static List<LanguageOptionDto> Project(Dictionary<string, LanguageBucket> map)
        {
            return map.Values
                .OrderByDescending(bucket => bucket.Count)
                .ThenBy(bucket => bucket.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(bucket => new LanguageOptionDto
                {
                    Code = bucket.Code,
                    Label = bucket.Label,
                    StreamCount = bucket.Count
                })
                .ToList();
        }

        private sealed class LanguageBucket
        {
            public LanguageBucket(string code, string label)
            {
                Code = code;
                Label = string.IsNullOrWhiteSpace(label) ? code.ToUpperInvariant() : label;
            }

            public string Code { get; }

            public string Label { get; }

            public int Count { get; private set; }

            public void Increment()
            {
                Count++;
            }
        }
    }

    private string? DescribeScope(RuleScope? scope)
    {
        return scope switch
        {
            RuleScope.Series => "Series rule applied",
            RuleScope.Library => "Library rule applied",
            RuleScope.Global => "Global rule applied",
            _ => "Rule applied"
        };
    }

    private Task SendGeneralCommandAsync(
        SessionInfo session,
        GeneralCommandType commandType,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var command = new GeneralCommand
        {
            Name = commandType,
            ControllingUserId = session.UserId
        };
        command.Arguments["Index"] = streamIndex.ToString(CultureInfo.InvariantCulture);

        return _sessionManager.SendGeneralCommand(
            session.Id,
            session.Id,
            command,
            cancellationToken);
    }
}
