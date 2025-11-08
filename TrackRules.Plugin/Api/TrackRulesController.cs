using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackRules.Core;
using MediaBrowser.Controller.Entities;
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
        ILogger<TrackRulesController> logger)
    {
        _ruleStore = ruleStore;
        _resolver = resolver;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _sessionManager = sessionManager;
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
