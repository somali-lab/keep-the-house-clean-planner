using System.Text.Json.Serialization;
using Huishoudplanner.Domain.Badges;

namespace Huishoudplanner.Adapters.Http.Badges;

/// <summary>The rule of a badge as the API shows it; an on-time-weeks rule has no <c>taskIds</c>.</summary>
public sealed record BadgeRuleResponse(string Type, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TaskIds, int Threshold);

/// <summary>Where the stored picture of a badge is served from, and what identifies its bytes; the bytes themselves are never in a badge.</summary>
public sealed record BadgeImageResponse(string ContentType, int Size, string Hash, string Url);

/// <summary>A badge as the API shows it.</summary>
public sealed record BadgeResponse(
    string Id,
    string Name,
    string Description,
    BadgeRuleResponse Rule,
    bool Active,
    string? ExampleKey,
    BadgeImageResponse? Image,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static BadgeResponse From(Badge badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        var rule = badge.Rule.Type == BadgeRuleType.OnTimeWeeks
            ? new BadgeRuleResponse(BadgeNames.ToWire(badge.Rule.Type), null, badge.Rule.Threshold)
            : new BadgeRuleResponse(BadgeNames.ToWire(badge.Rule.Type), badge.Rule.TaskIds, badge.Rule.Threshold);
        var image = badge.Image is { } info
            ? new BadgeImageResponse(info.ContentType.ContentType(), info.Size, info.Hash, $"/api/v2/badges/{badge.Id}/image?v={info.Version}")
            : null;
        return new BadgeResponse(badge.Id, badge.Name, badge.Description, rule, badge.Active, badge.ExampleKey, image, badge.CreatedAt, badge.UpdatedAt);
    }
}

/// <summary>One page of badges, oldest first; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record BadgeListResponse(IReadOnlyList<BadgeResponse> Items, string? NextCursor);

/// <summary>The answer to a delete.</summary>
public sealed record BadgeDeletedResponse(bool Deleted);

/// <summary>What adding the example badges did: the badges it created, and the number of examples that already existed.</summary>
public sealed record AddExampleBadgesResponse(IReadOnlyList<BadgeResponse> Created, int Skipped);

/// <summary>One award: a person holds a badge since the moment the data first crossed the threshold.</summary>
public sealed record BadgeAwardResponse(string Id, string BadgeId, string PersonId, DateTimeOffset AwardedAt)
{
    internal static BadgeAwardResponse From(BadgeAward award)
    {
        ArgumentNullException.ThrowIfNull(award);
        return new BadgeAwardResponse(award.Id, award.BadgeId, award.PersonId, award.AwardedAt);
    }
}

/// <summary>One page of awards, oldest first; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record BadgeAwardListResponse(IReadOnlyList<BadgeAwardResponse> Items, string? NextCursor);

/// <summary>How far a person is towards one active badge: <c>current</c> can exceed <c>threshold</c>, <c>awardedAt</c> is <c>null</c> while it is not earned.</summary>
public sealed record BadgeProgressItemResponse(string BadgeId, int Current, int Threshold, DateTimeOffset? AwardedAt);

/// <summary>The progress of one person towards every active badge, evaluated on the data at the moment of the request.</summary>
public sealed record BadgeProgressResponse(string PersonId, IReadOnlyList<BadgeProgressItemResponse> Items)
{
    internal static BadgeProgressResponse From(BadgeProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new BadgeProgressResponse(progress.PersonId, [.. progress.Items.Select(i => new BadgeProgressItemResponse(i.BadgeId, i.Current, i.Threshold, i.AwardedAt))]);
    }
}

/// <summary>The rule of a badge in a request: <c>executions</c> or <c>minutes</c> with the chosen tasks (none = every task), or <c>onTimeWeeks</c>.</summary>
public sealed record BadgeRuleRequest(string Type, IReadOnlyList<string>? TaskIds, int Threshold);

/// <summary>An uploaded picture: PNG, JPEG or WebP, at most 256 KB, the bytes as base64 text.</summary>
public sealed record BadgeImageRequest(string ContentType, string Data);

/// <summary>The body of <c>POST /api/v2/badges</c>. The name is trimmed (1 to 60 characters), the description at most 200, the badge is active unless said otherwise.</summary>
public sealed record CreateBadgeRequest(string Name, string? Description, BadgeRuleRequest Rule, bool? Active, BadgeImageRequest? Image);

/// <summary>The body of <c>PATCH /api/v2/badges/{id}</c>: every field is optional, <c>image: null</c> removes the picture, a body without fields changes nothing.</summary>
public sealed record UpdateBadgeRequest(string? Name, string? Description, BadgeRuleRequest? Rule, bool? Active, BadgeImageRequest? Image);

/// <summary>The body of <c>POST /api/v2/badges/examples</c>; without a body the language is Dutch.</summary>
public sealed record AddExampleBadgesRequest(string? Language);
